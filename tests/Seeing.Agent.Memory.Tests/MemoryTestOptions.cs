using Microsoft.Extensions.Options;
using Seeing.Agent.Memory.Configuration;

namespace Seeing.Agent.Memory.Tests;

internal sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
{
    public StaticOptionsMonitor(T value) => CurrentValue = value;

    public T CurrentValue { get; private set; }
    public T Get(string? name) => CurrentValue;

    public IDisposable OnChange(Action<T, string?> listener) => new Noop();

    public void Update(T value) => CurrentValue = value;

    private sealed class Noop : IDisposable
    {
        public void Dispose() { }
    }
}

internal static class MemoryTestOptions
{
    public static IOptionsMonitor<MemoryOptions> Monitor(MemoryOptions? options = null) =>
        new StaticOptionsMonitor<MemoryOptions>(options ?? new MemoryOptions());
}
