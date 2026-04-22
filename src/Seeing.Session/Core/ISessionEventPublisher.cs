using System;

namespace Seeing.Session.Core
{
    /// <summary>
    /// Simple interface for publishing session events.
    /// Exposes an observable stream of SessionEvent and a Publish method to emit events.
    /// </summary>
    public interface ISessionEventPublisher
    {
        /// <summary>
        /// Observable stream of session events.
        /// </summary>
        IObservable<SessionEvent> Events { get; }

        /// <summary>
        /// Publish a new session event to all subscribers.
        /// </summary>
        /// <param name="sessionEvent">The event to publish.</param>
        void Publish(SessionEvent sessionEvent);
    }
}
