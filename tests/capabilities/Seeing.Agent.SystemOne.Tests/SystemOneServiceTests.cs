using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.SystemOne;
using Seeing.Agent.SystemOne.Configuration;
using Seeing.Agent.SystemOne.Runtime;
using Xunit;

namespace Seeing.Agent.SystemOne.Tests;

public class SystemOneServiceTests
{
    [Fact]
    public async Task EvaluateAsync_使用默认Provider()
    {
        var registry = new FakeSystemOneProviderRegistry();
        var provider = new StubSystemOneProvider { Id = "typesafe" };
        registry.DefaultProvider = provider;
        registry.Items.Add(provider);
        var calls = 0;
        provider.Client.OnEvaluate = (_, _) =>
        {
            calls++;
            return Task.FromResult(new SystemOneResponse());
        };
        var sut = new SystemOneService(registry);

        await sut.EvaluateAsync(new SystemOneRequest(), TestContext.Current.CancellationToken);

        calls.Should().Be(1);
    }

    [Fact]
    public async Task EvaluateAsync_无默认Provider_抛InvalidOperationException()
    {
        var sut = new SystemOneService(new FakeSystemOneProviderRegistry());

        var act = async () => await sut.EvaluateAsync(new SystemOneRequest(), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ListModelsAsync_指定Provider_使用该Provider()
    {
        var registry = new FakeSystemOneProviderRegistry();
        var providerA = new StubSystemOneProvider { Id = "a" };
        var providerB = new StubSystemOneProvider { Id = "b" };
        registry.DefaultProvider = providerA;
        registry.Items.Add(providerA);
        registry.Items.Add(providerB);

        var bModels = new List<SystemOneModel> { new() { Name = "bm" } };
        providerB.Client.OnListModels = _ => Task.FromResult<IReadOnlyList<SystemOneModel>>(bModels);
        var sut = new SystemOneService(registry);

        var result = await sut.ListModelsAsync("b", TestContext.Current.CancellationToken);

        result.Should().BeSameAs(bModels);
    }

    [Fact]
    public async Task ListModelsAsync_指定Provider_忽略大小写()
    {
        var registry = new FakeSystemOneProviderRegistry();
        var provider = new StubSystemOneProvider { Id = "TypeSafe" };
        registry.DefaultProvider = provider;
        registry.Items.Add(provider);
        var models = new List<SystemOneModel> { new() { Name = "m" } };
        provider.Client.OnListModels = _ => Task.FromResult<IReadOnlyList<SystemOneModel>>(models);
        var sut = new SystemOneService(registry);

        var result = await sut.ListModelsAsync("typesafe", TestContext.Current.CancellationToken);

        result.Should().BeSameAs(models);
    }

    [Fact]
    public async Task ListModelsAsync_未指定_使用默认Provider()
    {
        var registry = new FakeSystemOneProviderRegistry();
        var provider = new StubSystemOneProvider { Id = "typesafe" };
        registry.DefaultProvider = provider;
        registry.Items.Add(provider);
        var models = new List<SystemOneModel> { new() { Name = "m" } };
        provider.Client.OnListModels = _ => Task.FromResult<IReadOnlyList<SystemOneModel>>(models);
        var sut = new SystemOneService(registry);

        var result = await sut.ListModelsAsync(null, TestContext.Current.CancellationToken);

        result.Should().BeSameAs(models);
    }

    [Fact]
    public async Task ListModelsAsync_指定不存在_抛InvalidOperationException()
    {
        var sut = new SystemOneService(new FakeSystemOneProviderRegistry());

        var act = async () => await sut.ListModelsAsync("missing", TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task 默认Provider选择_Env优先于TypeSafe()
    {
        using var env = SystemOneTestEnv.Scope(apiKey: "k", provider: "custom");
        var manager = CreateManager(new Dictionary<string, SystemOneProviderConfig>
        {
            ["custom"] = new() { Id = "custom", Type = SystemOneProviderTypes.TypeSafe, ApiKey = "k" }
        });

        var result = await new SystemOneService(manager).ListModelsAsync(
            null, TestContext.Current.CancellationToken);

        result.Single().Name.Should().Be("custom");
    }

    [Fact]
    public async Task 默认Provider选择_回退TypeSafe()
    {
        using var env = SystemOneTestEnv.Scope(apiKey: "k");
        var manager = CreateManager(new Dictionary<string, SystemOneProviderConfig>());

        var result = await new SystemOneService(manager).ListModelsAsync(
            null, TestContext.Current.CancellationToken);

        result.Single().Name.Should().Be("typesafe");
    }

    [Fact]
    public async Task 默认Provider选择_无TypeSafe时取首个()
    {
        using var env = SystemOneTestEnv.Scope();
        var manager = CreateManager(new Dictionary<string, SystemOneProviderConfig>
        {
            ["alpha"] = new() { Id = "alpha", Type = SystemOneProviderTypes.TypeSafe, ApiKey = "k" }
        });

        var result = await new SystemOneService(manager).ListModelsAsync(
            null, TestContext.Current.CancellationToken);

        result.Single().Name.Should().Be("alpha");
    }

    private static SystemOneProviderManager CreateManager(
        Dictionary<string, SystemOneProviderConfig> configs)
    {
        var store = new SystemOneConfigStore(new TestConfigSectionStore(configs));
        var factory = new StubSystemOneClientFactory
        {
            ClientFactory = config => new StubSystemOneClient
            {
                ProviderId = config.Id,
                OnListModels = _ => Task.FromResult<IReadOnlyList<SystemOneModel>>(
                    new List<SystemOneModel> { new() { Name = config.Id } })
            }
        };
        var manager = new SystemOneProviderManager(
            store,
            new ISystemOneClientFactory[] { factory },
            NullLogger<SystemOneProviderManager>.Instance);
        manager.Reload();
        return manager;
    }
}
