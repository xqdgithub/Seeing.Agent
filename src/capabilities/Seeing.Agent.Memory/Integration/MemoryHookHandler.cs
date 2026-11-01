using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Configuration;
using Seeing.Agent.Memory.Core.Models;
using Seeing.Session.Core;

namespace Seeing.Agent.Memory.Integration;

/// <summary>
/// Chat Hook：过滤后写入会话缓冲，不立即抽取。
/// </summary>
public sealed class ChatMemoryHandler : IHookHandler
{
    private readonly ISessionMemoryBuffer _buffer;
    private readonly IMemoryFlushService _flush;
    private readonly IMemoryHeuristicFilter _filter;
    private readonly IOptionsMonitor<MemoryOptions> _options;
    private readonly ISessionActivityTracker _activity;
    private readonly ILogger<ChatMemoryHandler>? _logger;

    public HookSpec Spec => HookRegistry.ChatAfterComplete;
    public int Priority => 10;

    public ChatMemoryHandler(
        ISessionMemoryBuffer buffer,
        IMemoryFlushService flush,
        IMemoryHeuristicFilter filter,
        IOptionsMonitor<MemoryOptions> options,
        ISessionActivityTracker activity,
        ILogger<ChatMemoryHandler>? logger = null)
    {
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        _flush = flush ?? throw new ArgumentNullException(nameof(flush));
        _filter = filter ?? throw new ArgumentNullException(nameof(filter));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));
        _logger = logger;
    }

    public Task<HookResult> ExecuteAsync(HookPayload payload)
    {
        try
        {
            var opts = _options.CurrentValue;
            if (!opts.Capture.AutoCapture || !opts.Capture.CaptureChat)
                return Task.FromResult(HookResult.Success);

            var content = HookDataContract.ChatAfterComplete.Content.GetFrom(payload.Result);
            if (string.IsNullOrWhiteSpace(content))
                return Task.FromResult(HookResult.Success);

            var max = Math.Max(1, opts.Capture.MaxSnippetChars);
            var snippet = content.Length <= max ? content : content[..max];
            var sessionId = string.IsNullOrEmpty(payload.SessionId) ? "unknown" : payload.SessionId;

            var candidate = new MemoryCandidate(
                Guid.NewGuid().ToString("N"),
                sessionId,
                AgentId: null,
                MemorySource.Chat,
                ToolId: null,
                snippet,
                DateTimeOffset.UtcNow);

            var decision = _filter.Evaluate(candidate);
            if (!decision.Accepted)
                return Task.FromResult(HookResult.Success);

            var overflow = _buffer.Add(candidate);
            _activity.Touch(sessionId);

            if (overflow is not null)
                _flush.TryEnqueueBatch(overflow);

            return Task.FromResult(HookResult.Success);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "ChatMemoryHandler failed");
            return Task.FromResult(HookResult.Success);
        }
    }
}

/// <summary>
/// Tool Hook：默认 CaptureTools=false，不捕获工具输出。
/// </summary>
public sealed class ToolMemoryHandler : IHookHandler
{
    private readonly IOptionsMonitor<MemoryOptions> _options;
    private readonly ILogger<ToolMemoryHandler>? _logger;

    public HookSpec Spec => HookRegistry.ToolExecuteAfter;
    public int Priority => 10;

    public ToolMemoryHandler(
        IOptionsMonitor<MemoryOptions> options,
        ILogger<ToolMemoryHandler>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
    }

    public Task<HookResult> ExecuteAsync(HookPayload payload)
    {
        try
        {
            var opts = _options.CurrentValue;
            if (!opts.Capture.AutoCapture || !opts.Capture.CaptureTools)
                return Task.FromResult(HookResult.Success);

            // 显式开启工具捕获时仍不入缓冲：本版本不支持工具批处理，避免回归高频 LLM。
            _logger?.LogDebug(
                "CaptureTools enabled but tool capture is disabled in batch extraction mode; Tool={ToolId}",
                HookDataContract.ToolExecuteAfter.ToolId.GetFrom(payload.Input));
            return Task.FromResult(HookResult.Success);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "ToolMemoryHandler failed");
            return Task.FromResult(HookResult.Success);
        }
    }
}

/// <summary>
/// 顶层 Agent 一轮完成后的终态观察者：累计轮次，达到阈值则 flush。
/// <para>
/// 历史实现挂在 <c>agent.after_invoke</c> 钩子上，但该钩子点在全仓没有触发点，
/// 导致 <see cref="MemoryExtractionOptions.ExtractEveryNTurns"/> 从未生效。
/// 现改由 <see cref="IExecutionOutcomeObserver"/> 承载：终态由执行链在
/// <c>ExecutionRunner</c> 的 finally 中统一扇出，本处理器只需消费已有的
/// <see cref="ExecutionOutcome"/> 事实，不回读会话、不做 I/O。
/// </para>
/// </summary>
public sealed class AgentTurnMemoryHandler : IExecutionOutcomeObserver
{
    private readonly IMemoryFlushService _flush;
    private readonly IOptionsMonitor<MemoryOptions> _options;
    private readonly ILogger<AgentTurnMemoryHandler>? _logger;

    public AgentTurnMemoryHandler(
        IMemoryFlushService flush,
        IOptionsMonitor<MemoryOptions> options,
        ILogger<AgentTurnMemoryHandler>? logger = null)
    {
        _flush = flush ?? throw new ArgumentNullException(nameof(flush));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
    }

    /// <summary>
    /// 判定本轮终态是否计入记忆轮次节奏。
    /// <para>
    /// 六条门禁（全部满足才观察）：抽取开关开启、轮次阈值 &gt; 0、本轮真正执行了 Agent
    /// （排除命令短路与纯系统批次）、属于顶层会话（排除子代理会话）、来源不是复盘会话自身、
    /// 终态为 <see cref="ExecutionStatus.Completed"/>（失败/中断不复盘）、以及会话 ID 非空。
    /// </para>
    /// </summary>
    /// <param name="outcome">本轮执行的终态事实。</param>
    /// <returns><c>true</c> 表示需要触发一次轮次 flush。</returns>
    public bool ShouldObserve(ExecutionOutcome outcome)
    {
        var extraction = _options.CurrentValue.Extraction;
        return extraction.Enabled
            && extraction.ExtractEveryNTurns > 0
            && outcome.AgentExecuted
            && outcome.IsTopLevel
            && outcome.Origin != SessionOrigin.Reflection
            && outcome.Status == ExecutionStatus.Completed
            && !string.IsNullOrWhiteSpace(outcome.SessionId);
    }

    /// <summary>
    /// 终态回调：达到轮次阈值时尝试 flush 该会话缓冲。
    /// <para>异常一律在内部消化并记日志，绝不外抛——观察者失败不得影响执行链。</para>
    /// </summary>
    /// <param name="outcome">本轮执行的终态事实。</param>
    /// <param name="ct">取消令牌（本处理为同步入队，不观察取消）。</param>
    public Task OnExecutionCompletedAsync(ExecutionOutcome outcome, CancellationToken ct = default)
    {
        try
        {
            var opts = _options.CurrentValue;
            if (!opts.Extraction.Enabled || opts.Extraction.ExtractEveryNTurns <= 0)
                return Task.CompletedTask;

            var sessionId = outcome.SessionId;
            if (string.IsNullOrWhiteSpace(sessionId))
                return Task.CompletedTask;

            _flush.TryFlushAfterTurn(sessionId);
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "AgentTurnMemoryHandler failed");
            return Task.CompletedTask;
        }
    }
}
