using Microsoft.Extensions.Logging;
using Seeing.Agent.Configuration;
using Seeing.Agent.Scheduler.Engine;
using Seeing.Agent.Scheduler.Management;

namespace Seeing.Agent.Scheduler.Configuration;

/// <summary>
/// 工作区切换时停掉调度器；重载完成后按新工作区从零重建（任务快照 + Quartz 触发器）。
/// <para>
/// <b>只保留参与者这一条路径</b>：不注册 <c>IReloadHandler&lt;WorkspaceChange&gt;</c>。
/// 两者会在同一轮里各做一次「停 → 清 → 重建 → 起」，顺序无法保证，任务会被注册两遍或漏注册。
/// </para>
/// <para>
/// 「停引擎」由这里做，「清内存状态」由 <see cref="ScheduleManager.ResetForWorkspaceAsync"/> 做；
/// Quartz 库里只剩宿主级触发器缓存，因此 <see cref="IQuartzSchedulerLifecycle.RebuildAsync"/>
/// 只需丢弃实例，不再需要按工作区重建工厂。
/// </para>
/// </summary>
public sealed class SchedulerWorkspaceParticipant : IWorkspaceApplyParticipant
{
    private readonly IQuartzSchedulerLifecycle _engine;
    private readonly ScheduleManager? _manager;
    private readonly ILogger<SchedulerWorkspaceParticipant> _logger;

    /// <summary>创建参与者。</summary>
    /// <param name="engine">Quartz 生命周期（停 / 重建 / 起）。</param>
    /// <param name="logger">日志。</param>
    /// <param name="manager">
    /// 调度管理器（DI 恒提供）。声明为可选只为保持「只关心引擎」的轻量构造可用；
    /// 生产路径必须经它重载新版 <c>jobs.json</c>，否则引擎起来了也没有任务。
    /// </param>
    public SchedulerWorkspaceParticipant(
        IQuartzSchedulerLifecycle engine,
        ILogger<SchedulerWorkspaceParticipant> logger,
        ScheduleManager? manager = null)
    {
        _engine = engine;
        _logger = logger;
        _manager = manager;
    }

    /// <summary>切换前调度器是否在运行（由 <see cref="OnApplyingAsync"/> 记录）。</summary>
    public bool WasStarted { get; set; }

    /// <inheritdoc/>
    public string ComponentId => "scheduler";

    /// <inheritdoc/>
    public async Task OnApplyingAsync(WorkspaceApplyContext context, CancellationToken ct)
    {
        WasStarted = _engine.IsStarted;

        // 顺序是「清 → 停」而不是「停 → 清」：quartz.db 已改为用户级固定库，
        // 旧工作区的持久触发器必须从**活着的**调度器上 DeleteJob 掉；
        // 一旦 Shutdown，旧实例就不能再执行删除，于是旧任务会按原 cron 继续触发。
        if (_manager is not null)
            await _manager.ResetForWorkspaceAsync(ct).ConfigureAwait(false);

        await _engine.StopAsync(ct).ConfigureAwait(false);

        _logger.LogInformation("工作区切换：调度器已停止（切换前运行={WasStarted}）", WasStarted);
    }

    /// <inheritdoc/>
    public async Task OnAppliedAsync(WorkspaceApplyContext context, CancellationToken ct)
    {
        await _engine.RebuildAsync(ct).ConfigureAwait(false);

        if (WasStarted)
        {
            if (_manager is not null)
            {
                // 经管理器启动：重新读新工作区的 jobs.json → 注册用户任务与心跳 → 起引擎。
                await _manager.StartAsync(ct).ConfigureAwait(false);
            }
            else
            {
                await _engine.StartAsync(ct).ConfigureAwait(false);
            }
        }

        _logger.LogInformation("工作区切换：调度器已按新工作区重建: {Root}", context.NewRoot);
    }
}
