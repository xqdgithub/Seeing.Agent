using System.Reactive.Subjects;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Session.Core;

namespace Seeing.Agent.Memory.Integration.Adapters;

public sealed class SessionEventPublisherAdapter : IMemorySessionEvents, IDisposable
{
    private readonly Subject<string> _ended = new();
    private readonly IDisposable? _subscription;

    public SessionEventPublisherAdapter(ISessionEventPublisher? publisher = null)
    {
        if (publisher is null)
            return;

        _subscription = publisher.Events.Subscribe(ev =>
        {
            if (ev.Type == SessionEventType.Destroyed && !string.IsNullOrWhiteSpace(ev.SessionId))
                _ended.OnNext(ev.SessionId);
        });
    }

    public IObservable<string> SessionEnded => _ended;

    public void Dispose()
    {
        _subscription?.Dispose();
        _ended.Dispose();
    }
}
