using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core;
using Seeing.Agent.Core.Models;
using Xunit;

namespace Seeing.Agent.Tests.Configuration;

public class SubAgentTaskDenyInjectionTests
{
    [Fact]
    public async Task RegisterAgentAsync_SubAgentMissingTaskDeny_AutoInjectsTask()
    {
        var store = new AgentStore(NullLogger<AgentStore>.Instance);
        var runtime = new Mock<IAgentRuntimeManager>();
        var workspace = new WorkspaceProvider(Path.GetTempPath());

        var manager = new AgentManager(
            NullLogger<AgentManager>.Instance,
            store,
            runtime.Object,
            workspace);

        var agent = new AgentDefinition
        {
            Name = "custom-sub",
            Mode = AgentMode.SubAgent,
            DeniedTools = new List<string>()
        };

        await manager.RegisterAgentAsync(agent);

        agent.DeniedTools.Should().Contain("task");
        var loaded = await store.GetAsync("custom-sub");
        loaded!.DeniedTools.Should().Contain("task");
    }

    [Fact]
    public async Task RegisterAgentAsync_Primary_DoesNotInjectTask()
    {
        var store = new AgentStore(NullLogger<AgentStore>.Instance);
        var runtime = new Mock<IAgentRuntimeManager>();
        var workspace = new WorkspaceProvider(Path.GetTempPath());

        var manager = new AgentManager(
            NullLogger<AgentManager>.Instance,
            store,
            runtime.Object,
            workspace);

        var agent = new AgentDefinition
        {
            Name = "custom-primary",
            Mode = AgentMode.Primary,
            DeniedTools = new List<string>()
        };

        await manager.RegisterAgentAsync(agent);

        agent.DeniedTools.Should().NotContain("task");
    }
}
