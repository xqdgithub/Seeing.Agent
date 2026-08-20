using FluentAssertions;
using Seeing.Agent.Abstractions.Configuration;
using Xunit;

namespace Seeing.Agent.Tests.Configuration;

public class ReloadSignalBusTests
{
    private sealed class FakeSignal : IReloadSignal { }
    private sealed class FakeHandler : IReloadHandler
    {
        public string ComponentId => "fake";
        public IReadOnlyList<Type> ChangeTypes { get; } = new[] { typeof(FakeSignal) };
        public Task ReloadAsync(IReloadSignal change, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public void PublishAsync_返回结果集合()
    {
        var iface = typeof(IReloadSignalBus);
        var method = iface.GetMethod(nameof(IReloadSignalBus.PublishAsync))!;
        method.ReturnType.Should().Be(typeof(Task<IReadOnlyList<ReloadResult>>));
        method.GetParameters()[0].ParameterType.Should().Be(typeof(IReloadSignal));
    }

    [Fact]
    public void Registry_提供注册与注销()
    {
        typeof(IReloadHandlerRegistry).GetMethod(nameof(IReloadHandlerRegistry.RegisterHandler))!.GetParameters()[0]
            .ParameterType.Should().Be(typeof(IReloadHandler));
        typeof(IReloadHandlerRegistry).GetMethod(nameof(IReloadHandlerRegistry.UnregisterHandler))!.GetParameters()[0]
            .ParameterType.Should().Be(typeof(IReloadHandler));
    }

    [Fact]
    public void ReloadResult_含诊断字段()
    {
        var result = new ReloadResult { ComponentId = "c", Success = true, Error = null, Duration = TimeSpan.FromMilliseconds(5) };
        result.ComponentId.Should().Be("c");
        result.Success.Should().BeTrue();
        result.Duration.Should().Be(TimeSpan.FromMilliseconds(5));
    }
}
