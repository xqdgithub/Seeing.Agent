namespace Seeing.Agent.Scheduler.Engine;

/// <summary>
/// 调度器生命周期（供工作区切换停/重建）。
/// <para>
/// Quartz 的 SQLite 库固定为**用户级**路径 <c>~/.seeing/scheduler/quartz.db</c>
/// （<c>Scheduler.Persistence.ConnectionString</c> 显式给时覆盖），不再随工作区变化。
/// 工作区切换时仍需丢弃已构造的 scheduler 实例：持久触发器表要按新工作区的
/// <c>jobs.json</c> 重新对账，避免旧工作区触发器在新调度器里复活。
/// </para>
/// </summary>
public interface IQuartzSchedulerLifecycle
{
    /// <summary>调度器是否已启动。</summary>
    bool IsStarted { get; }

    /// <summary>停止调度器。</summary>
    Task StopAsync(CancellationToken ct = default);

    /// <summary>启动调度器。</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>丢弃已构造的 scheduler 实例；下次 <see cref="StartAsync"/> 按当前工作区重建。</summary>
    Task RebuildAsync(CancellationToken ct = default);
}
