using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Configuration;
using Seeing.Agent.Memory.Core.Models;

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
            if (!opts.Enabled || !opts.Capture.AutoCapture || !opts.Capture.CaptureChat)
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
            if (!opts.Enabled || !opts.Capture.AutoCapture || !opts.Capture.CaptureTools)
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
/// 顶层 Agent 完成后累计轮次，达到阈值则 flush。
/// </summary>
public sealed class AgentTurnMemoryHandler : IHookHandler
{
    private readonly IMemoryFlushService _flush;
    private readonly IOptionsMonitor<MemoryOptions> _options;
    private readonly ILogger<AgentTurnMemoryHandler>? _logger;

    public HookSpec Spec => HookRegistry.AgentAfterInvoke;
    public int Priority => 10;

    public AgentTurnMemoryHandler(
        IMemoryFlushService flush,
        IOptionsMonitor<MemoryOptions> options,
        ILogger<AgentTurnMemoryHandler>? logger = null)
    {
        _flush = flush ?? throw new ArgumentNullException(nameof(flush));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
    }

    public Task<HookResult> ExecuteAsync(HookPayload payload)
    {
        try
        {
            var opts = _options.CurrentValue;
            if (!opts.Enabled || !opts.Extraction.Enabled || opts.Extraction.ExtractEveryNTurns <= 0)
                return Task.FromResult(HookResult.Success);

            var sessionId = payload.SessionId;
            if (string.IsNullOrWhiteSpace(sessionId))
                return Task.FromResult(HookResult.Success);

            _flush.TryFlushAfterTurn(sessionId);
            return Task.FromResult(HookResult.Success);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "AgentTurnMemoryHandler failed");
            return Task.FromResult(HookResult.Success);
        }
    }
}
