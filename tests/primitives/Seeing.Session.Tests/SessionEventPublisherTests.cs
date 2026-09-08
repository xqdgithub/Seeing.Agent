using System;
using System.Collections.Generic;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Session.Tests
{
    // Minimal in-test observable to avoid dependency on System.Reactive
    internal class SimpleObservable<T> : IObservable<T>
    {
        private readonly List<IObserver<T>> _observers = new List<IObserver<T>>();

        public IDisposable Subscribe(IObserver<T> observer)
        {
            if (!_observers.Contains(observer))
                _observers.Add(observer);
            return new Unsubscriber(_observers, observer);
        }

        public void Publish(T value)
        {
            foreach (var obs in _observers.ToArray())
            {
                obs.OnNext(value);
            }
        }

        private class Unsubscriber : IDisposable
        {
            private readonly List<IObserver<T>> _observers;
            private readonly IObserver<T> _observer;
            public Unsubscriber(List<IObserver<T>> observers, IObserver<T> observer)
            {
                _observers = observers;
                _observer = observer;
            }
            public void Dispose()
            {
                if (_observer != null && _observers.Contains(_observer))
                    _observers.Remove(_observer);
            }
        }
    }

    // Simple test publisher implementing the interface
    internal class TestSessionEventPublisher : ISessionEventPublisher
    {
        private readonly SimpleObservable<SessionEvent> _subject = new SimpleObservable<SessionEvent>();
        public IObservable<SessionEvent> Events => _subject;

        public void Publish(SessionEvent sessionEvent)
        {
            _subject.Publish(sessionEvent);
        }
    }

    public class SessionEventPublisherTests
    {
        [Fact]
        public void Events_Returns_IObservable_SessionEvent() 
        {
            var publisher = new TestSessionEventPublisher();
            Assert.NotNull(publisher.Events);
            Assert.True(publisher.Events is IObservable<SessionEvent>);
        }

        [Fact]
        public void Publish_Triggers_Event_For_Subscriber()
        {
            var publisher = new TestSessionEventPublisher();
            var received = new List<SessionEvent>();
            var observer = new ActionObserver<SessionEvent>(e => received.Add(e));
            var subscription = publisher.Events.Subscribe(observer);

            var evt = new SessionEvent { SessionId = "sess-1", Type = SessionEventType.Created };
            publisher.Publish(evt);

            Assert.Single(received);
            Assert.Equal("sess-1", received[0].SessionId);
            subscription.Dispose();
        }

        [Fact]
        public void Multiple_Subscribers_Receive_Event()
        {
            var publisher = new TestSessionEventPublisher();
            var a = new List<SessionEvent>();
            var b = new List<SessionEvent>();
            var subA = publisher.Events.Subscribe(new ActionObserver<SessionEvent>(e => a.Add(e)));
            var subB = publisher.Events.Subscribe(new ActionObserver<SessionEvent>(e => b.Add(e)));

            var evt = new SessionEvent { SessionId = "sess-2", Type = SessionEventType.Updated };
            publisher.Publish(evt);

            Assert.Single(a);
            Assert.Single(b);
            Assert.Equal("sess-2", a[0].SessionId);
            Assert.Equal("sess-2", b[0].SessionId);

            subA.Dispose();
            subB.Dispose();
        }
    }
}

// Simple IObserver wrapper for action-based callbacks in tests
internal class ActionObserver<T> : IObserver<T>
{
    private readonly Action<T> _onNext;
    public ActionObserver(Action<T> onNext) => _onNext = onNext;
    public void OnCompleted() { }
    public void OnError(Exception error) { }
    public void OnNext(T value) => _onNext(value);
}
