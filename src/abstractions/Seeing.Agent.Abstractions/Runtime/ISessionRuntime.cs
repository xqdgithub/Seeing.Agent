using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Execution;

namespace Seeing.Agent.Abstractions.Runtime;

public interface ISessionRuntime
{
    /// <summary>唯一驱动入口：投递一条入站消息（写入 + 激活）。</summary>
    Task<InboxResult> DeliverAsync(InboxMessage message, CancellationToken ct = default);

    /// <summary>
    /// 投递一条入站消息，并等待**本次投递所消费的那一轮执行**进入终态，返回其事实快照。
    /// <para>
    /// 与 <see cref="WaitForIdleAsync"/> 的区别（这是本方法存在的理由）：
    /// </para>
    /// <list type="bullet">
    /// <item><see cref="WaitForIdleAsync"/> 只保证「会话当前空闲」，既不知道是**哪一轮**跑完的，
    /// 也无法与并发投递对齐——调用方只能轮询并用「猜最近一次执行」的方式补足，连续投递时天然歧义。</item>
    /// <item>本方法把等待**绑定到 executionId**：返回的 outcome 就是本次投递所在的执行，
    /// 连续多次投递不会串台；且不会在无执行发生时静默返回。</item>
    /// </list>
    /// <para>
    /// 语义约定：
    /// </para>
    /// <list type="bullet">
    /// <item>仅接受 <see cref="InboxDelivery.Wake"/>：<see cref="InboxDelivery.Inject"/> /
    /// <see cref="InboxDelivery.Notify"/> 不产生执行，等待必然落空，直接抛 <see cref="ArgumentException"/>。</item>
    /// <item>投递失败（<see cref="InboxResult.Success"/> 为 <c>false</c>）抛 <see cref="InvalidOperationException"/>。</item>
    /// <item><paramref name="ct"/> 取消、运行时释放、或等待期间会话被 <see cref="StopAsync"/> 停止时，
    /// 抛 <see cref="OperationCanceledException"/>（不会永久挂起）。</item>
    /// <item>被中断（<see cref="InterruptAsync"/>）或执行失败仍算「终态」，照常返回对应
    /// <see cref="ExecutionStatus"/> 的 outcome，不抛异常——是否可接受由调用方判定。</item>
    /// </list>
    /// <para>
    /// 默认实现直接抛 <see cref="NotSupportedException"/>：未接入消费循环的替身不应静默成功，
    /// 以免调用方误以为拿到了真实结果。
    /// </para>
    /// </summary>
    /// <param name="message">待投递的入站消息（须为 Wake 投递）。</param>
    /// <param name="ct">取消令牌：取消即放弃等待。</param>
    /// <returns>本次投递所消费的那一轮执行的终态快照。</returns>
    Task<ExecutionOutcome> DeliverAndAwaitAsync(InboxMessage message, CancellationToken ct = default)
        => throw new NotSupportedException(
            $"{nameof(ISessionRuntime)} 的实现未支持 {nameof(DeliverAndAwaitAsync)}。");

    /// <summary>硬停当前轮：取消当前执行体；consumed 不回退，循环继续消费剩余未读。</summary>
    Task<bool> InterruptAsync(string sessionId, CancellationToken ct = default);

    /// <summary>
    /// 丢弃未消费入站：与消费循环同一把会话锁内标记 consumed 并保存，返回条数。
    /// 不启动消费循环。默认实现为无操作，供尚未接入的测试替身编译。
    /// </summary>
    Task<int> DiscardPendingAsync(string sessionId, CancellationToken ct = default)
        => Task.FromResult(0);

    /// <summary>停止会话：取消当前轮 + 终止消费循环 + 不再消费剩余未读；级联子会话。</summary>
    Task<int> StopAsync(string sessionId, CancellationToken ct = default);

    Task<int> CancelAllInFlightAsync(CancellationToken ct = default);
    Task WaitForIdleAsync(string sessionId, CancellationToken ct = default);
    SessionExecutionOverview GetOverview(string sessionId);
    bool HasAnyActiveExecution();
    IReadOnlyList<IMessageEvent> GetBufferedEvents(string sessionId);
    IAsyncEnumerable<IMessageEvent> SubscribeEvents(string sessionId, CancellationToken ct = default);
    Task<int> ReconcileIncompleteTasksAsync(string sessionId, CancellationToken ct = default);
}
