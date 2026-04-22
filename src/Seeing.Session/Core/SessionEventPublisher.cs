using System;
using System.Collections.Generic;

namespace Seeing.Session.Core
{
    /// <summary>
    /// 简单的事件发布器实现 - 不依赖 System.Reactive
    /// </summary>
    public class SessionEventPublisher : ISessionEventPublisher
    {
        private readonly List<IObserver<SessionEvent>> _observers = new();

        /// <summary>
        /// 可订阅的事件流
        /// </summary>
        public IObservable<SessionEvent> Events => new SessionEventObservable(_observers);

        /// <summary>
        /// 发布事件到所有订阅者
        /// </summary>
        /// <param name="sessionEvent">要发布的事件</param>
        public void Publish(SessionEvent sessionEvent)
        {
            foreach (var observer in _observers.ToArray())
            {
                observer.OnNext(sessionEvent);
            }
        }

        private class SessionEventObservable : IObservable<SessionEvent>
        {
            private readonly List<IObserver<SessionEvent>> _observers;

            public SessionEventObservable(List<IObserver<SessionEvent>> observers)
            {
                _observers = observers;
            }

            public IDisposable Subscribe(IObserver<SessionEvent> observer)
            {
                if (!_observers.Contains(observer))
                    _observers.Add(observer);
                return new Unsubscriber(_observers, observer);
            }

            private class Unsubscriber : IDisposable
            {
                private readonly List<IObserver<SessionEvent>> _observers;
                private readonly IObserver<SessionEvent> _observer;

                public Unsubscriber(List<IObserver<SessionEvent>> observers, IObserver<SessionEvent> observer)
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
    }
}