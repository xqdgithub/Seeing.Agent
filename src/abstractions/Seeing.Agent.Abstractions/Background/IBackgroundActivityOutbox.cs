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
}
