namespace Seeing.Agent.Abstractions.Execution;

/// <summary>
/// 进程级在途执行边界 — 供热重载 Deactivate 推迟 / 强制取消消费。
/// <para>由 <c>ExecutionJobService</c> 实现；无执行引擎的宿主可不注册（视为无在途）。</para>
/// </summary>
public interface IExecutionInFlightBoundary
{
    /// <summary>是否存在未终态（Running / Pending / Queued）执行。</summary>
    bool HasInFlight();

    /// <summary>当前未终态执行 id 快照。</summary>
    IReadOnlyList<string> ListInFlightExecutionIds();

    /// <summary>取消全部未终态执行；返回成功取消数量。</summary>
    Task<int> CancelAllInFlightAsync(CancellationToken cancellationToken = default);
}
