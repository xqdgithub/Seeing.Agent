using FluentAssertions;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Core.Llm;
using Seeing.Agent.Hosting.Commands;
using Xunit;

namespace Seeing.Agent.Hosting.Tests.Commands;

public class AgentCommandsTests
{
    private static AgentCommands Create()
    {
        var agents = new Mock<IAgentRegistry>();
        agents.Setup(a => a.GetPrimaryAgentsAsync())
            .ReturnsAsync(new List<AgentDefinition> { new() { Name = "build" } });

        var llm = new Mock<ILlmService>();
        llm.Setup(l => l.GetAvailableModels())
            .Returns(new Dictionary<string, ModelConfig> { ["m1"] = new() { Id = "m1", Name = "M1" } });

        return new AgentCommands(agents.Object, llm.Object);
    }

    [Fact]
    public void Model_No_Args_Should_Request_Picker()
    {
        var result = Create().SwitchModel(new CommandContext());
        result.Action!.Name.Should().Be(CommandActionNames.ModelPicker);
        result.ShouldContinue.Should().BeFalse();
    }

    [Fact]
    public void Model_With_Args_Should_Request_Set_And_Not_Claim_Switched()
    {
        var result = Create().SwitchModel(new CommandContext { Arguments = "m1" });
        result.Action!.Name.Should().Be(CommandActionNames.ModelSet);
        result.Action.Payload!["modelId"].Should().Be("m1");
        result.Message.Should().NotContain("Switched to model");
    }

    [Fact]
    public async Task Agent_With_Args_Should_Request_Set()
    {
        var result = await Create().SwitchAgent(new CommandContext { Arguments = "build" });
        result.Action!.Name.Should().Be(CommandActionNames.AgentSet);
        result.Action.Payload!["agentId"].Should().Be("build");
    }
}
