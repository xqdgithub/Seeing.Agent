using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.SystemOne;
using Seeing.Agent.SystemOne.Runtime;
using Xunit;

namespace Seeing.Agent.SystemOne.Tests;

public class ConfiguredSystemOneProviderTests
{
    private static SystemOneProviderConfig Config() => new()
    {
        Id = "typesafe",
        Name = "TS",
        Type = SystemOneProviderTypes.TypeSafe,
        ApiKey = "k",
        MaxRetries = 3
    };

    [Fact]
    public void 属性_应透传配置()
    {
        var factory = new StubSystemOneClientFactory();
        using var sut = new ConfiguredSystemOneProvider(Config(), factory, NullLogger.Instance);

        sut.Id.Should().Be("typesafe");
        sut.Name.Should().Be("TS");
        sut.Type.Should().Be(SystemOneProviderTypes.TypeSafe);
        sut.MaxRetries.Should().Be(3);
    }

    [Fact]
    public void GetClient_应延迟创建且复用同一实例()
    {
        var factory = new StubSystemOneClientFactory();
        using var sut = new ConfiguredSystemOneProvider(Config(), factory, NullLogger.Instance);

        factory.CreateCount.Should().Be(0);

        var first = sut.GetClient();
        var second = sut.GetClient();

        factory.CreateCount.Should().Be(1);
        first.Should().BeSameAs(second);
    }

    [Fact]
    public void Dispose_已创建客户端_应释放客户端()
    {
        var client = new StubSystemOneClient();
        var factory = new StubSystemOneClientFactory { ClientFactory = _ => client };
        var sut = new ConfiguredSystemOneProvider(Config(), factory, NullLogger.Instance);
        _ = sut.GetClient();

        sut.Dispose();

        client.DisposeCount.Should().Be(1);
    }

    [Fact]
    public void Dispose_未创建客户端_不应触发工厂()
    {
        var factory = new StubSystemOneClientFactory();
        var sut = new ConfiguredSystemOneProvider(Config(), factory, NullLogger.Instance);

        var act = () => sut.Dispose();

        act.Should().NotThrow();
        factory.CreateCount.Should().Be(0);
    }

    [Fact]
    public void GetClient_已Dispose_应抛ObjectDisposedException()
    {
        var factory = new StubSystemOneClientFactory();
        var sut = new ConfiguredSystemOneProvider(Config(), factory, NullLogger.Instance);
        sut.Dispose();

        var act = () => sut.GetClient();

        act.Should().Throw<ObjectDisposedException>()
            .Which.ObjectName.Should().Be(nameof(ConfiguredSystemOneProvider));
    }

    [Fact]
    public void Dispose_重复调用_不应抛出且仅释放一次()
    {
        var client = new StubSystemOneClient();
        var factory = new StubSystemOneClientFactory { ClientFactory = _ => client };
        var sut = new ConfiguredSystemOneProvider(Config(), factory, NullLogger.Instance);
        _ = sut.GetClient();

        var act = () =>
        {
            sut.Dispose();
            sut.Dispose();
        };

        act.Should().NotThrow();
        client.DisposeCount.Should().Be(1);
    }
}
