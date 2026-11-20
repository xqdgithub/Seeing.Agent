namespace Seeing.Agent.Abstractions.Background;

/// <summary>
/// 待投递活动的落盘队列（进程内 at-least-once：先落盘再投递）。
/// </summary>
public interface IBackgroundActivityOutbox
{
    /// <summary>当前待投递条数。</summary>
    int PendingCount { get; }

    /// <summary>
    /// 执行一轮泵取（内部互斥闸），返回本轮处理的条数。
    /// <para>测试可单步驱动本方法，无需等待合并窗口。</para>
    /// </summary>
    Task<int> PumpOnceAsync(CancellationToken ct = default);

    /// <summary>
    /// 宿主启动时清理上一代实例留下的待投递批次（spec §10.4）：把
    /// <c>Status == Pending</c> 且宿主进程已死的条目标 <c>Dropped</c>，返回受影响条数。
    /// <para>
    /// 存活实例（含本实例）的 pending 一律不碰——它们的宿主会自己投递。
    /// </para>
    /// <para>
    /// 调用时机由宿主决定（启动钩子）；本接口只提供一次性扫描能力，不做定时清理。
    /// </para>
    /// </summary>
    Task<int> ReconcileDeadHostsAsync(CancellationToken ct = default);
}
