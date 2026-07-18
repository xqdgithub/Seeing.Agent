using Seeing.Agent.Memory.Abstractions;

namespace Seeing.Agent.Memory.Integration.Adapters;

public sealed class NullMemorySessionEvents : IMemorySessionEvents
{
    public IObservable<string> SessionEnded { get; } = Never.Instance;

    private sealed class Never : IObservable<string>
    {
        public static readonly Never Instance = new();
        public IDisposable Subscribe(IObserver<string> observer) => Nop.Instance;
    }

    private sealed class Nop : IDisposable
    {
        public static readonly Nop Instance = new();
        public void Dispose() { }
    }
}
