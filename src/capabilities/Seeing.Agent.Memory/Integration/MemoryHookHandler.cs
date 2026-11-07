using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Configuration;
using Seeing.Agent.Memory.Core.Models;
using Seeing.Agent.Memory.Integration.Hosting;
using Seeing.Session.Core;

namespace Seeing.Agent.Memory.Integration;

/// <summary>
/// LLM Hook：过滤后写入会话缓冲，不立即抽取。
/// </summary>
public sealed class ChatMemoryHandler : IHookHandler
{
    private readonly ISessionMemoryBuffer _buffer;
    private readonly IMemoryFlushService _flush;
    private readonly IMemoryHeuristicFilter _filter;
    private readonly IOptionsMonitor<MemoryOptions> _options;
    private readonly ISessionActivityTracker _activity;
    private readonly ISessionManager? _sessionManager;
    private readonly ILogger<ChatMemoryHandler>? _logger;

    public HookSpec Spec => HookRegistry.LlmAfterComplete;
    public int Priority => 10;

    public ChatMemoryHandler(
        ISessionMemoryBuffer buffer,
        IMemoryFlushService flush,
        IMemoryHeuristicFilter filter,
        IOptionsMonitor<MemoryOptions> options,
        ISessionActivityTracker activity,
        ISessionManager? sessionManager = null,
        ILogger<ChatMemoryHandler>? logger = null)
    {
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        _flush = flush ?? throw new ArgumentNullException(nameof(flush));
        _filter = filter ?? throw new ArgumentNullException(nameof(filter));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));
        // 可选：缺失时无法做来源过滤（见下方注释），仅在无会话管理器的独立装配下发生。
        _sessionManager = sessionManager;
        _logger = logger;
    }

    public Task<HookResult> ExecuteAsync(HookPayload payload)
    {
        try
        {
            // 只收 chat 正文（决议 1 / spec §7.6）：非 chat 能力的 llm.after_complete 载荷
            // 里没有会话对话内容，收进缓冲会让记忆抽取去消化向量数、音频字节这类东西。
            if (!IsChatPayload(payload))
                return Task.FromResult(HookResult.Success);

            var opts = _options.CurrentValue;
            if (!opts.Capture.AutoCapture || !opts.Capture.CaptureChat)
                return Task.FromResult(HookResult.Success);

            var content = HookDataContract.LlmAfterComplete.Content.GetFrom(payload.Result);
            if (string.IsNullOrWhiteSpace(content))
                return Task.FromResult(HookResult.Success);

            var max = Math.Max(1, opts.Capture.MaxSnippetChars);
            var snippet = content.Length <= max ? content : content[..max];
            var sessionId = string.IsNullOrEmpty(payload.SessionId) ? "unknown" : payload.SessionId;

            // 平台内部会话的 assistant 输出不是用户对话内容，收进记忆会污染召回：
            // 复盘会话产出的是分析结论，且它随后会被删除——记进来的条目连会话都查不到。
            // 这里读内存中的会话（Get 不触发磁盘加载）；读不到时保守收录（不误杀）。
            if (_sessionManager?.Get(sessionId) is { } session
                && !session.Origin.IsUserVisibleTaskOrigin())
            {
                return Task.FromResult(HookResult.Success);
            }

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

    /// <summary>
    /// 判定载荷是否 chat 能力（严格口径：键缺失或值非 chat 一律跳过）。
    /// <para>与 <c>BudgetUpdateHook</c> 里同形的三行判定：<c>llm.*</c> 载荷的读取方都是
    /// 能力包，够不到 Core，也不该往 Abstractions 塞实现（零实现纪律），因此各自本地判定。</para>
    /// </summary>
    private static bool IsChatPayload(HookPayload payload)
    {
        var key = HookDataContract.LlmAfterComplete.Capability.Name;

        string? value = null;
        if (payload.Result.TryGetValue(key, out var fromResult))
            value = fromResult as string;
        if (value is null && payload.Input.TryGetValue(key, out var fromInput))
            value = fromInput as string;

        return LlmCapabilityMap.TryParseHookValue(value, out var capability)
            && capability == LlmCapabilityKind.Chat;
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
/// <para>
/// <b>与模块生命周期的关系</b>：本观察者由 DI 注册，<c>ExecutionRunner</c> 不感知模块启停，
/// 因此停用期间仍会被扇出。故 <see cref="ShouldObserve"/> 额外读
/// <see cref="MemoryModuleActivity.IsActive"/> 自行门控——否则停用后轮次继续增长、
/// batch 积压在有界 channel 里，重新激活时一次性消费大量过期数据。
/// </para>
/// </summary>
public sealed class AgentTurnMemoryHandler : IExecutionOutcomeObserver
{
    private readonly IMemoryFlushService _flush;
    private readonly IOptionsMonitor<MemoryOptions> _options;
    private readonly ILogger<AgentTurnMemoryHandler>? _logger;
    private readonly MemoryModuleActivity? _activity;

    /// <param name="flush">轮次 flush 端口。</param>
    /// <param name="options">记忆配置（热重载）。</param>
    /// <param name="logger">可选日志。</param>
    /// <param name="activity">
    /// 模块运行门控；<c>null</c> 表示未注入（测试或独立使用场景），视为活跃，
    /// 避免把「无模块生命周期」误判成停用。
    /// </param>
    public AgentTurnMemoryHandler(
        IMemoryFlushService flush,
        IOptionsMonitor<MemoryOptions> options,
        ILogger<AgentTurnMemoryHandler>? logger = null,
        MemoryModuleActivity? activity = null)
    {
        _flush = flush ?? throw new ArgumentNullException(nameof(flush));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
        _activity = activity;
    }

    /// <summary>
    /// 判定本轮终态是否计入记忆轮次节奏。
    /// <para>
    /// 七条门禁（全部满足才观察）：抽取开关开启、轮次阈值 &gt; 0、本轮真正执行了 Agent
    /// （排除命令短路与纯系统批次）、属于顶层会话（排除子代理会话）、来源不是复盘会话自身、
    /// 终态为 <see cref="ExecutionStatus.Completed"/>（失败/中断不复盘）、会话 ID 非空；
    /// 另加一条模块运行态门控（模块停用时不观察）。
    /// </para>
    /// <para>
    /// <b>关于 <see cref="SessionOrigin.Reflection"/> 门禁</b>：当前实现下该条件被
    /// <see cref="ExecutionOutcome.IsTopLevel"/> 完全蕴含——复盘子会话只能经
    /// <c>SessionGroupManager.CreateInternalAsync</c> → <c>CreateChildCoreAsync</c> 创建，
    /// 那里硬性 <c>Kind = SessionKind.SubAgent</c>，而 <see cref="ExecutionOutcome.IsTopLevel"/>
    /// 的真值正是 <c>Kind != SubAgent</c>，故 <c>Origin == Reflection</c> 的轮次必然
    /// <c>IsTopLevel == false</c>。这是 <b>spec §4.2 与实现冲突时的双保险</b>
    /// （spec 主张复盘子会话的 <c>IsTopLevel</c> 为 <c>true</c>，当前实现断言为 <c>false</c>）；
    /// 一旦将来按 spec 修正 <c>IsTopLevel</c> 语义，本条立即成为唯一有效判定。
    /// <b>当前属非可达路径</b>，不得因「测不到」而删除。
    /// </para>
    /// <para>
    /// <b>关于 <see cref="_activity"/> 为 <c>null</c></b>：刻意视为「活跃」而非「停用」。
    /// 取舍理由：<c>null</c> 只出现在测试或独立使用场景（生产路径由
    /// <c>AddMemoryServices</c> 无条件 <c>TryAddSingleton</c> 登记），若按停用处理，
    /// 这些场景会静默失效且难以定位。代价是：若将来生产环境漏登记
    /// <c>MemoryModuleActivity</c>，停用门控会静默失效——该风险已登记为已知取舍，
    /// 不通过「改为必填参数」解决（会牵连多处构造点，成本大于收益）。
    /// </para>
    /// <para>
    /// <b>已知行为差异</b>：<see cref="SessionOrigin.Scheduler"/> / <see cref="SessionOrigin.Heartbeat"/>
    /// 在仓库中尚无赋值点（T4 债），心跳/cron 任务默认跑在用户主会话上，
    /// 因此这些轮次<b>会被计入</b>记忆轮次节奏。这是可接受的现状：它们确实是真实执行轮次。
    /// </para>
    /// <para>
    /// <b>遗留</b>：<c>SessionForker.ForkAsync</c> 不继承 <see cref="SessionData.Origin"/>，
    /// 分叉出的会话恒为默认 <see cref="SessionOrigin.User"/>（T4/Session 层遗留），
    /// 与本观察者的门禁判定无关但需知悉。
    /// </para>
    /// </summary>
    /// <param name="outcome">本轮执行的终态事实。</param>
    /// <returns><c>true</c> 表示需要触发一次轮次 flush。</returns>
    public bool ShouldObserve(ExecutionOutcome outcome)
    {
        var extraction = _options.CurrentValue.Extraction;
        return _activity?.IsActive != false
            && extraction.Enabled
            && extraction.ExtractEveryNTurns > 0
            && outcome.AgentExecuted
            && outcome.IsTopLevel
            && outcome.Origin != SessionOrigin.Reflection
            && outcome.Status == ExecutionStatus.Completed
            && !string.IsNullOrWhiteSpace(outcome.SessionId);
    }

    /// <summary>
    /// 终态回调：门禁通过时尝试 flush 该会话缓冲。
    /// <para>
    /// 门禁统一走 <see cref="ShouldObserve"/>（单一真相源）：宿主先调 <c>ShouldObserve</c>
    /// 再调本方法，但公开契约允许直接调用，故此处再判一次，避免两处口径漂移。
    /// </para>
    /// <para>
    /// <b>失败/中断轮次的数据不丢</b>：用户消息早已由 <c>ChatMemoryHandler</c> 写入会话缓冲
    /// （chat hook 早于终态），本处只是把 flush 时机推迟到
    /// <see cref="MemoryExtractionOptions.FlushIdleMinutes"/>（默认 5 分钟）的空闲 flush。
    /// 若把 <see cref="ExecutionStatus.Cancelled"/> 也计入，用户频繁中断会加速 flush，
    /// 反而抽出大量半截对话。
    /// </para>
    /// <para>异常一律在内部消化并记日志，绝不外抛——观察者失败不得影响执行链。</para>
    /// </summary>
    /// <param name="outcome">本轮执行的终态事实。</param>
    /// <param name="ct">取消令牌（本处理为同步入队，不观察取消）。</param>
    public Task OnExecutionCompletedAsync(ExecutionOutcome outcome, CancellationToken ct = default)
    {
        try
        {
            // 门禁判定也在 try 内：类文档承诺「异常一律在内部消化，绝不外抛」，
            // 而 ShouldObserve 读取热重载配置（IOptionsMonitor.CurrentValue），
            // 不能假设它永不失抛。
            if (!ShouldObserve(outcome))
                return Task.CompletedTask;

            _flush.TryFlushAfterTurn(outcome.SessionId);
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "AgentTurnMemoryHandler failed");
            return Task.CompletedTask;
        }
    }
}
