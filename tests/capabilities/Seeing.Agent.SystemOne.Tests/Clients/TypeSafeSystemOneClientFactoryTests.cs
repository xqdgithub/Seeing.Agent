using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.SystemOne;
using Seeing.Agent.SystemOne.Clients;
using Xunit;

namespace Seeing.Agent.SystemOne.Tests.Clients;

public class TypeSafeSystemOneClientFactoryTests
{
    [Theory]
    [InlineData("typesafe")]
    [InlineData("TypeSafe")]
    [InlineData("TYPESAFE")]
    public void SupportsType_IsCaseInsensitive(string type)
    {
        var factory = CreateFactory();

        factory.SupportsType(type).Should().BeTrue();
    }

    [Fact]
    public void SupportsType_WithUnknownType_ReturnsFalse()
    {
        var factory = CreateFactory();

        factory.SupportsType("other").Should().BeFalse();
    }

    [Fact]
    public void SupportedTypes_ContainsTypeSafe()
    {
        var factory = CreateFactory();

        factory.SupportedTypes.Should().Contain(SystemOneProviderTypes.TypeSafe);
    }

    [Fact]
    public void Create_ReturnsTypeSafeClient()
    {
        var factory = CreateFactory();
        var config = new SystemOneProviderConfig
        {
            Id = "typesafe",
            Type = SystemOneProviderTypes.TypeSafe,
            BaseUrl = "http://systemone.test",
            ApiKey = "test-key",
            Model = "jev-latest"
        };

        using var client = (TypeSafeSystemOneClient)factory.Create(config);

        client.Should().BeOfType<TypeSafeSystemOneClient>();
        client.ProviderId.Should().Be("typesafe");
        client.ProviderType.Should().Be(SystemOneProviderTypes.TypeSafe);
    }

    private static TypeSafeSystemOneClientFactory CreateFactory()
        => new(NullLoggerFactory.Instance);
}
