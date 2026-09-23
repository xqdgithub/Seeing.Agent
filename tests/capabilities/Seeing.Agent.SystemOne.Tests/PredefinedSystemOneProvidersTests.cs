using FluentAssertions;
using Seeing.Agent.Abstractions.SystemOne;
using Xunit;

namespace Seeing.Agent.SystemOne.Tests;

public class PredefinedSystemOneProvidersTests
{
    [Fact]
    public void CreateDefaultTypeSafe_应返回内置默认值()
    {
        var config = PredefinedSystemOneProviders.CreateDefaultTypeSafe();

        config.Id.Should().Be("typesafe");
        config.Type.Should().Be(SystemOneProviderTypes.TypeSafe);
        config.Name.Should().Be("TypeSafe Jev");
        config.BaseUrl.Should().Be(SystemOneDefaults.DefaultBaseUrl);
        config.Model.Should().Be(SystemOneDefaults.DefaultModel);
        config.Timeout.Should().Be(SystemOneDefaults.DefaultTimeoutMs);
        config.MaxRetries.Should().Be(2);
        config.ApiKey.Should().BeNull();
    }

    [Fact]
    public void CreateDefaultTypeSafe_每次应返回独立实例()
    {
        var first = PredefinedSystemOneProviders.CreateDefaultTypeSafe();
        var second = PredefinedSystemOneProviders.CreateDefaultTypeSafe();

        first.Should().NotBeSameAs(second);
    }
}
