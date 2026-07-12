using Seeing.Agent.Core.Events;

namespace Seeing.Agent.App.Execution;

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