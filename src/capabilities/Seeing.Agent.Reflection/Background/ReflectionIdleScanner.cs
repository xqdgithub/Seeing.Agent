using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Reflection.Abstractions;
using Seeing.Agent.Reflection.Configuration;
using Seeing.Agent.Reflection.Core;
using Seeing.Session.Core;
using System.Collections.Concurrent;

namespace Seeing.Agent.Reflection.Background;

/// <summary>
/// 空闲会话复盘扫描器：把「哪些空闲会话值得复盘」这条策略从后台循环里独立出来。
/// <para>
/// 分离的理由是<b>可测性</b>而非整洁：策略里的每一道门禁（会话结构、来源、二次空闲确认、冷却）
/// 漏掉任意一道的后果都是「对不该复盘的会话做复盘」——这类缺陷不会报错，只会静静烧预算。
/// 埋在 <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> 的私有方法里就没有判别手段。
/// </para>
/// <para>
/// <b>注意这里的门禁与 <c>ReflectionObserver</c> 是两份实现</b>：观察者拿到的是
/// <see cref="Seeing.Agent.Abstractions.Execution.ExecutionOutcome"/>（终态事实），
/// 本类拿到的是会话本身，两者的可用信息不同，无法共用一段代码。
/// 但两者表达的约束必须一致：只复盘顶层、用户可见来源的会话。
/// </para>
/// </summary>
public sealed class ReflectionIdleScanner
{
    private readonly IReflectionQueue _queue;
    private readonly ISessionActivityTracker _activityTracker;
    private readonly ISessionManager? _sessionManager;
    private readonly IOptionsMonitor<ReflectionOptions> _options;
    private readonly ILogger<ReflectionIdleScanner>? _logger;

    /// <summary>
    /// 已投递过空闲复盘的会话 → 投递时的活动时刻。
    /// <para>
    /// 存在的意义是<b>冷却</b>：空闲集合是一份快照，不清除的话同一个空闲会话
    /// 每次扫描都会被重新入队，直到把 <c>MaxRunsPerHour</c> 与 <c>DailyTokenBudget</c> 烧穿。
    /// 用「活动时刻」而不是「上次复盘时刻」做键，是因为用户一旦回到会话，
    /// <see cref="ISessionActivityTracker.Touch"/> 会刷新时刻，据此天然允许下一次空闲复盘。
    /// </para>
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _dispatched = new(StringComparer.Ordinal);

    public ReflectionIdleScanner(
        IReflectionQueue queue,
        ISessionActivityTracker activityTracker,
        IOptionsMonitor<ReflectionOptions> options,
        ISessionManager? sessionManager = null,
        ILogger<ReflectionIdleScanner>? logger = null)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _activityTracker = activityTracker ?? throw new ArgumentNullException(nameof(activityTracker));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _sessionManager = sessionManager;
        _logger = logger;
    }

    /// <summary>
    /// 扫描一次空闲会话并入队。返回本次新投递的数量。
    /// </summary>
    /// <param name="now">当前时刻（注入以便测试控制，不读时钟）。</param>
    public int Scan(DateTimeOffset now)
    {
        var options = _options.CurrentValue;
        if (!options.Enabled || options.Mode == ReflectionMode.Off)
            return 0;

        var idle = TimeSpan.FromMinutes(Math.Max(1, options.IdleMinutes));
        var dispatched = 0;

        foreach (var sessionId in _activityTracker.GetIdleSessions(idle))
        {
            // 只读内存：TryGetOrLoadAsync 可能触发磁盘加载，后台扫描不应为此付出 IO。
            // 会话不在内存中（已淘汰）说明它已经很久没有活动迹象，没有复盘价值。
            if (_sessionManager?.Get(sessionId) is not { } session)
                continue;

            // 结构门禁：复盘对象必须是顶层会话。子代理轮次属于父轮次的内部细节。
            if (session.Kind != SessionKind.Root)
                continue;

            // 来源门禁：复盘自身必须排除（防递归）。用 IsAutomatableOrigin 而不是
            // IsUserVisibleTaskOrigin——定时 / 心跳会话虽然不出现在任务视图，
            // 但它们是真实的 agent 执行，同样可能产出可复用经验。
            if (!session.Origin.IsAutomatableOrigin())
                continue;

            // 二次确认仍然空闲：扫描到入队之间用户可能已经回到该会话。
            if (!_activityTracker.TryGetLastActivity(sessionId, out var lastActivity))
                continue;
            if (now - lastActivity < idle)
                continue;

            // 冷却：同一段空闲期内只投递一次。
            if (_dispatched.TryGetValue(sessionId, out var dispatchedAt) && dispatchedAt == lastActivity)
                continue;
            var result = _queue.TryEnqueue(new ReflectionRequest(
                    sessionId,
                    string.Empty,
                    ReflectionTriggerKind.Idle,
                    now));

            if (result is ReflectionEnqueueResult.Enqueued or ReflectionEnqueueResult.Updated)
            {
                _dispatched[sessionId] = lastActivity;
                if (result == ReflectionEnqueueResult.Enqueued)
                    dispatched++;
            }
            else if (result == ReflectionEnqueueResult.Dropped)
            {
                // 队列满时不记冷却：否则该会话会被永久跳过，等队列空出来也不会再试。
                _logger?.LogDebug("空闲复盘请求因队列已满被丢弃，下轮重试：SessionId={SessionId}", sessionId);
            }
        }

        // 清掉已不再空闲（用户回来了）或已消失的会话，避免字典随会话数无界增长。
        foreach (var pair in _dispatched)
        {
            if (!_activityTracker.TryGetLastActivity(pair.Key, out var last) || last != pair.Value)
                _dispatched.TryRemove(pair.Key, out _);
        }

        if (dispatched > 0)
            _logger?.LogDebug("空闲触发已投递复盘：{Count} 个会话", dispatched);

        return dispatched;
    }
}
