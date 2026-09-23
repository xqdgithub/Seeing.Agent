using FluentAssertions;
using Seeing.Agent.Abstractions.SystemOne;
using Xunit;

namespace Seeing.Agent.SystemOne.Tests;

public class SystemOneClientFactoryResolverTests
{
    private sealed class FakeClient : ISystemOneClient
    {
        public string ProviderId => "fake";
        public string ProviderType => "fake";

        public Task<SystemOneResponse> EvaluateAsync(SystemOneRequest request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<SystemOneModel>> ListModelsAsync(CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }

    private sealed class FakeFactory : ISystemOneClientFactory
    {
        private readonly ISystemOneClient _client;

        public FakeFactory(params string[] types)
            : this(new FakeClient(), types)
        {
        }

        public FakeFactory(ISystemOneClient client, params string[] types)
        {
            _client = client;
            SupportedTypes = new HashSet<string>(types, StringComparer.OrdinalIgnoreCase);
        }

        public IReadOnlySet<string> SupportedTypes { get; }

        public bool SupportsType(string type) => SupportedTypes.Contains(type);

        public ISystemOneClient Create(SystemOneProviderConfig config) => _client;
    }

    [Fact]
    public void Find_ReturnsFirstMatchingFactory()
    {
        var first = new FakeFactory("typesafe");
        var second = new FakeFactory("typesafe");
        var factories = new ISystemOneClientFactory[] { first, second };

        var result = SystemOneClientFactoryResolver.Find(factories, "typesafe");

        result.Should().BeSameAs(first);
    }

    [Fact]
    public void Find_ReturnsNull_WhenNoFactorySupportsType()
    {
        var factories = new ISystemOneClientFactory[] { new FakeFactory("typesafe") };

        SystemOneClientFactoryResolver.Find(factories, "other").Should().BeNull();
    }

    [Fact]
    public void Find_ReturnsNull_WhenTypeIsWhitespace()
    {
        var factories = new ISystemOneClientFactory[] { new FakeFactory("typesafe") };

        SystemOneClientFactoryResolver.Find(factories, "   ").Should().BeNull();
    }

    [Fact]
    public void Require_ReturnsMatchingFactory_CaseInsensitive()
    {
        var factory = new FakeFactory("typesafe");
        var factories = new ISystemOneClientFactory[] { factory };

        var result = SystemOneClientFactoryResolver.Require(factories, "TypeSafe");

        result.Should().BeSameAs(factory);
    }

    [Fact]
    public void Require_ThrowsNotSupportedException_ListingSupportedTypes()
    {
        var factories = new ISystemOneClientFactory[] { new FakeFactory("typesafe") };

        var act = () => SystemOneClientFactoryResolver.Require(factories, "unknown");

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*unknown*")
            .WithMessage("*已支持*typesafe*");
    }

    [Fact]
    public void Require_ThrowsArgumentException_WhenTypeIsWhitespace()
    {
        var factories = new ISystemOneClientFactory[] { new FakeFactory("typesafe") };

        var act = () => SystemOneClientFactoryResolver.Require(factories, "   ");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Require_ThrowsArgumentNullException_WhenFactoriesNull()
    {
        var act = () => SystemOneClientFactoryResolver.Require(null!, "typesafe");

        act.Should().Throw<ArgumentNullException>();
    }
}
