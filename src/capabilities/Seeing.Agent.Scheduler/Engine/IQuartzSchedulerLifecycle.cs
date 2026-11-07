namespace Seeing.Agent.Scheduler.Engine;

/// <summary>
/// 调度器生命周期（供工作区切换停/重建）。
/// <para>
/// 为什么需要「重建」：Quartz 的 SQLite 连接串在注册时写死为
/// <c>{ProjectSeeingDirectory}/quartz.db</c>，仅停再启仍会回到旧库。
/// 工作区切换必须丢弃已构造的 scheduler 实例，让下次启动按新根重新解析。
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
