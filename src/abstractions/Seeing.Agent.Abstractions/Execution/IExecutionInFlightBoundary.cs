namespace Seeing.Agent.Abstractions.Execution;

/// <summary>
/// 进程级在途执行边界 — 供热重载 Deactivate 推迟 / 强制取消消费。
/// <para>由 <c>SessionRuntime</c> 实现；无执行引擎的宿主可不注册（视为无在途）。</para>
/// </summary>
public interface IExecutionInFlightBoundary
{
    /// <summary>是否存在未终态（Running / Pending）执行。</summary>
    bool HasInFlight();

    /// <summary>当前未终态执行 id 快照。</summary>
    IReadOnlyList<string> ListInFlightExecutionIds();

    /// <summary>取消全部未终态执行；返回成功取消数量。</summary>
    Task<int> CancelAllInFlightAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 等到没有未终态执行为止。默认实现立即返回，供无执行引擎的替身使用。
    /// <para>
    /// 为什么需要它：<see cref="CancelAllInFlightAsync"/> 只是发出取消信号，
    /// 执行循环还要跑完收尾才会清掉未终态标记。切换工作区必须等到它真的落地。
    /// </para>
    /// </summary>
    Task WaitUntilIdleAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
