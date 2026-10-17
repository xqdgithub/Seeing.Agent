namespace Seeing.Agent.Abstractions.Events;

/// <summary>
/// Publisher for execution events, supporting multiple subscribers per session.
/// </summary>
public interface IExecutionEventPublisher
{
    /// <summary>
    /// Publishes an event to all subscribers of a session.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="evt">The event to publish.</param>
    void Publish(string sessionId, IMessageEvent evt);

    /// <summary>
    /// Subscribes to events for a specific session.
    /// </summary>
    /// <param name="sessionId">The session ID to subscribe to.</param>
    /// <param name="cancellationToken">Token to cancel the subscription.</param>
    /// <returns>An async enumerable of events.</returns>
    IAsyncEnumerable<IMessageEvent> SubscribeAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>
    /// 实时订阅（不回放）：同步注册即刻生效，仅接收注册之后发布的事件。
    /// <para>
    /// 供请求/应答消费方（IM 桥等）使用：须在触发执行的投递（唤醒）之前调用，
    /// 并配合轮次门控忽略上一轮仍在途的尾流。UI 重连类消费方应继续使用
    /// <see cref="SubscribeAsync"/>（含缓冲回放）。
    /// </para>
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <returns>实时订阅句柄；<see cref="IDisposable.Dispose"/> 注销。</returns>
    ILiveEventSubscription SubscribeLive(string sessionId);

    /// <summary>
    /// Gets buffered events for a session (for reconnection).
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <returns>List of buffered events.</returns>
    IReadOnlyList<IMessageEvent> GetBufferedEvents(string sessionId);

    /// <summary>
    /// Clears the event buffer for a session (called when execution reaches terminal state).
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    void ClearBuffer(string sessionId);

    /// <summary>
    /// Completes the event channel for a session (signals end of events).
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    void CompleteSession(string sessionId);
}
