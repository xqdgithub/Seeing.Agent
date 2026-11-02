using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Reflection.Abstractions;
using Seeing.Agent.Reflection.Configuration;
using Seeing.Agent.Reflection.Core;
using Seeing.Session.Core;
using System.Collections.Concurrent;

namespace Seeing.Agent.Reflection.Background;

/// <summary>
/// 复盘后台工作者：排空队列 + 周期性扫描空闲会话。
/// <para>
/// 单循环模型：每轮先把队列中的请求全部执行完（非阻塞 <c>TryDequeue</c>），
/// 再按需产出空闲触发请求，最后等待 1 分钟进入下一轮。
/// 相比「队列等待 + 定时器」双任务，单循环不会出现两条执行路径并发跑复盘。
/// </para>
/// </summary>
public sealed class ReflectionWorker : BackgroundService, IModuleHostedService
{
    /// <summary>模块 id（与 <see cref="ReflectionModule.Id"/> 对齐）。</summary>
    public const string ModuleId = "reflection";

    private static readonly TimeSpan s_tickInterval = TimeSpan.FromMinutes(1);

    private readonly IReflectionQueue _queue;
    private readonly IReflectionExecutor _executor;
    private readonly ISessionActivityTracker _activityTracker;
    private readonly Seeing.Session.Core.ISessionManager? _sessionManager;
    private readonly ReflectionModuleActivity _moduleActivity;
    private readonly IOptionsMonitor<ReflectionOptions> _options;
    private readonly ILogger<ReflectionWorker> _logger;
    private readonly ModuleHostedRunGate _run = new();

    /// <summary>
    /// 已投递过空闲复盘的会话 → 投递时的活动时刻。
    /// <para>
    /// 存在的意义是<b>冷却</b>：空闲集合是一份快照，不清除的话同一个空闲会话
    /// 每分钟都会被重新入队，直到把 <c>MaxRunsPerHour</c> 与 <c>DailyTokenBudget</c> 烧穿。
    /// 用「活动时刻」而不是「上次复盘时刻」做键，是因为用户一旦回到会话，
    /// <see cref="ISessionActivityTracker.Touch"/> 会刷新时刻，据此天然允许下一次空闲复盘。
    /// </para>
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _idleDispatched = new(StringComparer.Ordinal);

    public ReflectionWorker(
        IReflectionQueue queue,
        IReflectionExecutor executor,
        ISessionActivityTracker activityTracker,
        ReflectionModuleActivity moduleActivity,
        IOptionsMonitor<ReflectionOptions> options,
        ILogger<ReflectionWorker> logger,
        Seeing.Session.Core.ISessionManager? sessionManager = null)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _activityTracker = activityTracker ?? throw new ArgumentNullException(nameof(activityTracker));
        _moduleActivity = moduleActivity ?? throw new ArgumentNullException(nameof(moduleActivity));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        // 可选：缺失时只跳过空闲触发（队列触发不依赖他），保持能力包可独立装配。
        _sessionManager = sessionManager;
    }

    /// <inheritdoc cref="IModuleHostedService.ModuleId" />
    string IModuleHostedService.ModuleId => ModuleId;

    /// <inheritdoc />
    public bool IsRunning => _run.IsRunning;

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (_run.IsRunning || !_moduleActivity.IsActive)
            return Task.CompletedTask;

        if (!_run.TryBegin())
            return Task.CompletedTask;

        try
        {
            return base.StartAsync(cancellationToken);
        }
        catch
        {
            _run.End();
            throw;
        }
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _run.End();
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("复盘后台工作者已启动");

            // 时钟漂移容忍：PeriodicTimer 允许单次 tick 超时，不需要额外补 tick 逻辑。
            using var ticker = new PeriodicTimer(s_tickInterval);

            while (!stoppingToken.IsCancellationRequested && _moduleActivity.IsActive)
            {
                try
                {
                    await DrainQueueAsync(stoppingToken).ConfigureAwait(false);
                    EnqueueIdleSessions();
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // 单轮失败不得终止长循环：后台复盘是尽力而为的旁路能力。
                    _logger.LogWarning(ex, "复盘后台工作者本轮处理失败，将在下一轮重试");
                }

                try
                {
                    if (!await ticker.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                        break;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        finally
        {
            _run.End();
        }
    }

    /// <summary>非阻塞排空队列，逐个交给执行器。</summary>
    private async Task DrainQueueAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _moduleActivity.IsActive && _queue.TryDequeue(out var request))
        {
            try
            {
                var result = await _executor.RunAsync(request, ct).ConfigureAwait(false);
                if (!result.Executed)
                {
                    // 跳过属于正常业务结果（关闭/超预算/超频率/无来源/超时等），不能中断本轮排空。
                    _logger.LogDebug("复盘跳过：{Reason}", result.SkipReason);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "单次复盘执行失败：SessionId={SessionId}, Kind={Kind}",
                    request.SessionId,
                    request.Kind);
            }
        }
    }

    /// <summary>
    /// 按空闲阈值扫描会话并入队。
    /// <para>
    /// 这里的过滤<b>不能只有空闲判定</b>：活动追踪器对子代理会话与复盘会话同样会 Touch，
    /// 若不过滤来源，空闲触发会去复盘子代理甚至复盘会话自身——直接破坏
    /// 「只复盘顶层会话」与「防递归」两条硬约束。
    /// </para>
    /// </summary>
    private void EnqueueIdleSessions()
    {
        if (!_moduleActivity.IsActive)
            return;

        var options = _options.CurrentValue;
        if (!options.Enabled || options.Mode == ReflectionMode.Off)
            return;

        var idle = TimeSpan.FromMinutes(Math.Max(1, options.IdleMinutes));
        var sessionManager = _sessionManager;

        foreach (var sessionId in _activityTracker.GetIdleSessions(idle))
        {
            if (!_moduleActivity.IsActive)
                return;

            // 只读内存：TryGetOrLoadAsync 可能触发磁盘加载，后台扫描不应为此付出 IO。
            // 会话不在内存中（已淘汰）说明它已经空闲很久且没有活动迹象，没有复盘价值。
            if (sessionManager?.Get(sessionId) is not { } session)
                continue;

            // 与 ReflectionObserver.ShouldObserve 对齐的来源/结构门禁。
            // 复盘会话是 Kind=SubAgent 且 Origin=Reflection，两道判断都会挡住它；
            // 子代理会话（Kind=SubAgent / Origin=SubAgentTask）由 Kind 判断挡住。
            if (session.Kind != SessionKind.Root)
                continue;
            if (!session.Origin.IsUserVisibleTaskOrigin())
                continue;

            // 二次确认仍然空闲：扫描到入队之间用户可能已经回到该会话。
            if (!_activityTracker.TryGetLastActivity(sessionId, out var lastActivity))
                continue;
            if (DateTimeOffset.UtcNow - lastActivity < idle)
                continue;

            // 冷却：同一会话在同一段空闲期内只投递一次。
            if (_idleDispatched.TryGetValue(sessionId, out var dispatchedAt)
                && dispatchedAt == lastActivity)
            {
                continue;
            }

            if (_queue.TryEnqueue(new ReflectionRequest(
                    sessionId,
                    string.Empty,
                    ReflectionTriggerKind.Idle,
                    DateTimeOffset.UtcNow)))
            {
                _idleDispatched[sessionId] = lastActivity;
            }
        }

        // 清掉已不再空闲（用户回来了）的记录，避免字典随会话数无界增长。
        foreach (var pair in _idleDispatched)
        {
            if (!_activityTracker.TryGetLastActivity(pair.Key, out var last) || last != pair.Value)
                _idleDispatched.TryRemove(pair.Key, out _);
        }
    }
}
