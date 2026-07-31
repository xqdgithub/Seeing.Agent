namespace Seeing.Session.Core
{
    /// <summary>
    /// 简单的事件发布器实现 - 不依赖 System.Reactive
    /// </summary>
    public class SessionEventPublisher : ISessionEventPublisher
    {
        private readonly List<IObserver<SessionEvent>> _observers = new();
        private readonly object _observersLock = new();

        /// <summary>
        /// 可订阅的事件流
        /// </summary>
        public IObservable<SessionEvent> Events => new SessionEventObservable(_observers, _observersLock);

        /// <summary>
        /// 发布事件到所有订阅者
        /// </summary>
        /// <param name="sessionEvent">要发布的事件</param>
        public void Publish(SessionEvent sessionEvent)
        {
            IObserver<SessionEvent>[] snapshot;
            lock (_observersLock)
            {
                snapshot = _observers.ToArray();
            }
            foreach (var observer in snapshot)
            {
                observer.OnNext(sessionEvent);
            }
        }

        private class SessionEventObservable : IObservable<SessionEvent>
        {
            private readonly List<IObserver<SessionEvent>> _observers;
            private readonly object _lock;

            public SessionEventObservable(List<IObserver<SessionEvent>> observers, object lockObj)
            {
                _observers = observers;
                _lock = lockObj;
            }

            public IDisposable Subscribe(IObserver<SessionEvent> observer)
            {
                lock (_lock)
                {
                    if (!_observers.Contains(observer))
                        _observers.Add(observer);
                }
                return new Unsubscriber(_observers, _lock, observer);
            }

            private class Unsubscriber : IDisposable
            {
                private readonly List<IObserver<SessionEvent>> _observers;
                private readonly object _lock;
                private readonly IObserver<SessionEvent> _observer;

                public Unsubscriber(List<IObserver<SessionEvent>> observers, object lockObj, IObserver<SessionEvent> observer)
                {
                    _observers = observers;
                    _lock = lockObj;
                    _observer = observer;
                }

                public void Dispose()
                {
                    lock (_lock)
                    {
                        if (_observer != null && _observers.Contains(_observer))
                            _observers.Remove(_observer);
                    }
                }
            }
        }
    }
}