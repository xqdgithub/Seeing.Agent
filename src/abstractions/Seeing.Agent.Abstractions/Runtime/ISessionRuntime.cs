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
    /// <item>本方法把等待**绑定到本次投递的那一轮执行**（实现层按消息归属锚定 executionId），
    /// 返回的 outcome 就是本次投递所在的执行；连续多次投递不会串台，也不会在无执行发生时静默返回。</item>
    /// </list>
    /// <para>
    /// 语义约定：
    /// </para>
    /// <list type="bullet">
    /// <item>仅接受 <see cref="InboxDelivery.Wake"/> 且 presentation 不是
    /// <see cref="InboxPresentation.Assistant"/> / <see cref="InboxPresentation.None"/>：这两类
    /// 落盘即标记已消费，永不进入未读集合、不会触发执行，等待必然落空，直接抛
    /// <see cref="ArgumentException"/>。同样地 <see cref="InboxDelivery.Inject"/> /
    /// <see cref="InboxDelivery.Notify"/> 也被拒绝。</item>
    /// <item>投递失败（<see cref="InboxResult.Success"/> 为 <c>false</c>）抛 <see cref="InvalidOperationException"/>。</item>
    /// <item><paramref name="ct"/> 取消、运行时释放、或等待期间会话被 <see cref="StopAsync"/> 停止时，
    /// 抛 <see cref="OperationCanceledException"/>（不会永久挂起）。</item>
    /// <item>被中断（<see cref="InterruptAsync"/>）或执行失败仍算「终态」，照常返回对应
    /// <see cref="ExecutionStatus"/> 的 outcome，不抛异常——是否可接受由调用方判定。</item>
    /// <item>返回的 outcome <b>绝不会</b>处于 <see cref="ExecutionStatus.Running"/> /
    /// <see cref="ExecutionStatus.Pending"/>；实现保证只在终态组装，
    /// 且执行体抛异常时落定为 <see cref="ExecutionStatus.Failed"/>。</item>
    /// <item><see cref="ExecutionOutcome.Duration"/> 当前为墙钟口径（含排队与工具等待），
    /// 非「模型循环累计时长」；精确口径待执行链补齐后收敛。</item>
    /// </list>
    /// <para>
    /// 默认实现直接抛 <see cref="NotSupportedException"/>：未接入消费循环的替身不应静默成功，
    /// 以免调用方误以为拿到了真实结果。
    /// </para>
    /// </summary>
    /// <param name="message">待投递的入站消息（须为 Wake 投递）。</param>
    /// <param name="ct">取消令牌：取消即放弃等待。</param>
    /// <returns>本次投递所消费的那一轮执行的终态快照。</returns>
    /// <remarks>
    /// <para><b>已知边界：消息在落盘后被第三方移出会话时，本方法不会自行结束。</b></para>
    /// <para>
    /// 等待与「这条消息被哪一轮消费」绑定。若消息落盘后、被消费前，经会话编辑类能力
    /// （如 <c>SessionTrimTool</c> / <c>SessionReverter</c> 的 <c>ReplaceMessages</c>）从会话中删除，
    /// 消费循环将永远看不到它，也就不会有任何一轮执行来兑现本次等待。
    /// </para>
    /// <para>
    /// <b>为什么不做自动兜底</b>：唯一能在「无未读」时刻判定「这条消息彻底没人要了」的方式是
    /// 在该处取消等待槽，但那一刻「没有未读」只是瞬时事实（同批消息可能刚被上一轮消费完），
    /// 取消会误杀已兑现的等待——把偶发的误取消换成偶发的挂起，是更可控的取舍：
    /// 挂起可被调用方 <see cref="CancellationToken"/> 兜住，误取消则会让拿到错误结论。
    /// </para>
    /// <para>
    /// <b>调用方契约</b>：本方法可能在 <b>没有执行发生</b>的情况下长时间不返回，
    /// 因此<b>务必</b>传入带超时的 <paramref name="ct"/>（如 <c>new CancellationTokenSource(timeout)</c>），
    /// 不要依赖本方法自身收敛。
    /// </para>
    /// <para>
    /// <b>下列机制只对「已经建立起来的等待」有效</b>（即本方法已进入等待阶段之后）：
    /// <see cref="StopAsync"/>、<see cref="DiscardPendingAsync"/>、以及运行时释放（<c>Dispose</c>）
    /// 会主动结束这些等待。<b>它们不覆盖「已释放之后再投递」</b>：
    /// 运行时释放后调用本方法，消费循环会在取会话锁处直接退出，因此不会有任何一轮执行产生，
    /// 该次等待<b>不会</b>被自动取消——务必先确认运行时仍可用，或依赖 <paramref name="ct"/> 兜底。
    /// </para>
    /// </remarks>
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
