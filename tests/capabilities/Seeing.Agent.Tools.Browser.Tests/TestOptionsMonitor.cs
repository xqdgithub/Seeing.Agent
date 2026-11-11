using Microsoft.Extensions.Options;
using Seeing.Agent.Tools.Browser;

namespace Seeing.Agent.Tools.Browser.Tests;

/// <summary>测试用配置监视器（固定值）。</summary>
internal sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
{
    public TestOptionsMonitor(T value) => CurrentValue = value;

    public T CurrentValue { get; }

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
