using FluentAssertions;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Hosting.Execution;
using Xunit;

namespace Seeing.Agent.Hosting.Tests.Commands;

public class CommandActionPassthroughTests
{
    [Fact]
    public void CreateCommandResultEvent_Should_Carry_Action()
    {
        var action = new CommandAction
        {
            Name = CommandActionNames.ModelSet,
            Payload = new Dictionary<string, object?> { ["modelId"] = "m1" }
        };
        var result = new CommandResult { Success = true, Message = "ok", Action = action };

        var evt = ExecutionRunner.CreateCommandResultEvent("s1", "model", result);

        evt.CommandName.Should().Be("model");
        evt.Action.Should().NotBeNull();
        evt.Action!.Name.Should().Be(CommandActionNames.ModelSet);
        evt.Action.Payload!["modelId"].Should().Be("m1");
    }
}
