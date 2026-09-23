using FluentAssertions;
using Seeing.Agent.Abstractions.SystemOne;
using Seeing.Agent.SystemOne.Clients;
using Xunit;

namespace Seeing.Agent.SystemOne.Tests.Clients;

public class SystemOneHttpClientFactoryTests
{
    [Fact]
    public void Create_WithConfiguredTimeout_SetsHttpClientTimeout()
    {
        using var client = SystemOneHttpClientFactory.Create(new SystemOneProviderConfig { Timeout = 3000 });

        client.Timeout.Should().Be(TimeSpan.FromMilliseconds(3000));
    }

    [Fact]
    public void Create_WithDefaultConfig_UsesDefaultTimeout()
    {
        using var client = SystemOneHttpClientFactory.Create(new SystemOneProviderConfig());

        client.Timeout.Should().Be(TimeSpan.FromMilliseconds(SystemOneDefaults.DefaultTimeoutMs));
    }

    [Fact]
    public void Create_WithNonPositiveTimeout_FallsBackToDefault()
    {
        using var client = SystemOneHttpClientFactory.Create(new SystemOneProviderConfig { Timeout = 0 });

        client.Timeout.Should().Be(TimeSpan.FromMilliseconds(SystemOneDefaults.DefaultTimeoutMs));
    }

    [Fact]
    public void CreateHandler_ConfiguresPooledConnectionLifetimeAndProxy()
    {
        using var handler = SystemOneHttpClientFactory.CreateHandler(new SystemOneProviderConfig());

        handler.PooledConnectionIdleTimeout.Should().Be(TimeSpan.FromMinutes(2));
        handler.PooledConnectionLifetime.Should().Be(TimeSpan.FromMinutes(5));
        handler.UseProxy.Should().BeFalse();
    }
}
