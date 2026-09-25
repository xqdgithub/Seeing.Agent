using Microsoft.Extensions.DependencyInjection;
using FluentAssertions;
using Seeing.Agent.Abstractions.Agents;
using Xunit;

namespace Seeing.Agent.Agents.BuiltIn.Tests;

public class AgentsBuiltInActivateTests
{
    [Fact]
    public async Task ActivateAsync_RegistersBuiltInsOntoStore()
    {
        var store = new InMemoryAgentStore();
        var module = new AgentsBuiltInModule(store);

        store.Has("build").Should().BeFalse();

        await module.ActivateAsync(new ServiceCollection().BuildServiceProvider(), TestContext.Current.CancellationToken);

        var names = (await store.GetAllAsync()).Select(a => a.Name).ToList();
        names.Should().Contain(["build", "plan", "explore", "general", "summary"]);
    }

    [Fact]
    public async Task ActivateAsync_WithoutStore_Throws()
    {
        var module = new AgentsBuiltInModule();

        var act = () => module.ActivateAsync(new ServiceCollection().BuildServiceProvider(), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task DeactivateAsync_ShouldUnregisterBuiltIns()
    {
        var store = new InMemoryAgentStore();
        var module = new AgentsBuiltInModule(store);
        var services = new ServiceCollection().BuildServiceProvider();
        var ct = TestContext.Current.CancellationToken;

        await module.ActivateAsync(services, ct);
        (await store.GetAllAsync()).Should().NotBeEmpty();

        await module.DeactivateAsync(services, ct);

        foreach (var name in new[] { "build", "plan", "explore", "general", "summary" })
            store.Has(name).Should().BeFalse($"{name} 应随模块停用被注销");
        (await store.GetAllAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ActivateDeactivateActivate_ShouldBeIdempotent()
    {
        var store = new InMemoryAgentStore();
        var module = new AgentsBuiltInModule(store);
        var services = new ServiceCollection().BuildServiceProvider();
        var ct = TestContext.Current.CancellationToken;

        await module.ActivateAsync(services, ct);
        await module.DeactivateAsync(services, ct);
        await module.ActivateAsync(services, ct);

        (await store.GetAllAsync()).Select(a => a.Name)
            .Should().BeEquivalentTo(["build", "plan", "explore", "general", "summary"]);
    }

    private sealed class InMemoryAgentStore : IAgentStore
    {
        private readonly Dictionary<string, AgentDefinition> _agents = new(StringComparer.OrdinalIgnoreCase);

        public Task RegisterAsync(AgentDefinition agentInfo)
        {
            _agents[agentInfo.Name] = agentInfo;
            return Task.CompletedTask;
        }

        public bool Unregister(string name) => _agents.Remove(name);

        public Task<AgentDefinition?> GetAsync(string name)
            => Task.FromResult(_agents.TryGetValue(name, out var a) ? a : null);

        public Task<IReadOnlyList<AgentDefinition>> GetAllAsync()
            => Task.FromResult<IReadOnlyList<AgentDefinition>>(_agents.Values.ToList());

        public bool Has(string name) => _agents.ContainsKey(name);
    }
}
