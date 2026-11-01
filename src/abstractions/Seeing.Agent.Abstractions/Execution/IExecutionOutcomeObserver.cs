namespace Seeing.Agent.Abstractions.Execution;

/// <summary>
/// 一轮执行终态的观察者端口。
/// <para>
/// 由宿主在执行进入终态、组装好 <see cref="ExecutionOutcome"/> 后统一扇出调用；
/// 实现方（记忆轮次触发、自动反思等）只消费事实，不得回写执行状态或抛错阻断执行链。
/// </para>
/// <para>
/// 与 Hook 的区别：Hook 挂在执行**过程**的各个点上，本端口只在**终态**触发一次，
/// 且拿到的是已聚合的完整事实，无需自行推算步数与用量。
/// </para>
/// <para>
/// 语义约束：观察是"尽力而为"的旁路动作——实现的异常应自行吞掉并记日志，
/// 单个观察者失败不得影响其它观察者或本轮执行的终态结果。
/// </para>
/// </summary>
public interface IExecutionOutcomeObserver
{
    /// <summary>
    /// 判定本次终态是否需要被本观察者处理。
    /// <para>
    /// 默认实现放行全部终态；需要按子会话、执行标志或来源过滤的实现覆盖此方法即可。
    /// </para>
    /// </summary>
    /// <param name="outcome">本轮执行的终态事实。</param>
    /// <returns><c>true</c> 表示需要调用 <see cref="OnExecutionCompletedAsync"/>。</returns>
    bool ShouldObserve(ExecutionOutcome outcome) => true;

    /// <summary>
    /// 处理一轮已完成执行的终态事实。
    /// <para>仅在 <see cref="ShouldObserve"/> 返回 <c>true</c> 后调用，且不得因自身失败影响执行链。</para>
    /// </summary>
    /// <param name="outcome">本轮执行的终态事实。</param>
    /// <param name="ct">取消令牌。</param>
    Task OnExecutionCompletedAsync(ExecutionOutcome outcome, CancellationToken ct = default);
}
