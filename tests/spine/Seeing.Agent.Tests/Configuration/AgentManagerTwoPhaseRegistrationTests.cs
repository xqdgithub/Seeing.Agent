using Microsoft.Extensions.DependencyInjection;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Agents.BuiltIn;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core;
using Xunit;

namespace Seeing.Agent.Tests.Configuration;

public class AgentManagerTwoPhaseRegistrationTests
{
    [Fact]
    public void Ctor_HasNoIEnumerableAgentDefinitionParameter()
    {
        var ctors = typeof(AgentManager).GetConstructors();
        ctors.Should().NotBeEmpty();

        foreach (var ctor in ctors)
        {
            ctor.GetParameters()
                .Should()
                .NotContain(p =>
                    p.ParameterType == typeof(IEnumerable<AgentDefinition>)
                    || (p.ParameterType.IsGenericType
                        && p.ParameterType.GetGenericTypeDefinition() == typeof(IEnumerable<>)
                        && p.ParameterType.GetGenericArguments()[0] == typeof(AgentDefinition)),
                    "AgentManager must not accept built-in agents via IEnumerable<AgentDefinition>");
        }
    }

    [Fact]
    public async Task Ctor_StartsWithEmptyStore_BuiltInsAppearAfterModuleActivate()
    {
        var store = new AgentStore(NullLogger<AgentStore>.Instance);
        var runtime = new Mock<IAgentRuntimeManager>();
        var workspace = new WorkspaceProvider(Path.GetTempPath());

        var manager = new AgentManager(
            NullLogger<AgentManager>.Instance,
            store,
            runtime.Object,
            workspace);

        (await manager.GetAgentsAsync()).Should().BeEmpty();

        await new AgentsBuiltInModule(store).ActivateAsync(new ServiceCollection().BuildServiceProvider());

        var names = (await manager.GetAgentsAsync()).Select(a => a.Name).ToList();
        names.Should().Contain(["build", "plan", "explore", "general", "summary"]);
    }
}
