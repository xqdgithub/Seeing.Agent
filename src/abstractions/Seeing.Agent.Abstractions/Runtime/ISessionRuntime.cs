using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Execution;

namespace Seeing.Agent.Abstractions.Runtime;

public interface ISessionRuntime
{
    /// <summary>唯一驱动入口：投递一条入站消息（写入 + 激活）。</summary>
    Task<InboxResult> DeliverAsync(InboxMessage message, CancellationToken ct = default);

    /// <summary>硬停当前轮：取消当前执行体；consumed 不回退，循环继续消费剩余未读。</summary>
    Task<bool> InterruptAsync(string sessionId, CancellationToken ct = default);

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
