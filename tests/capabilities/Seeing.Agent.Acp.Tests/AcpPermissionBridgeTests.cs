using Acp.Messages;
using Acp.Types;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Acp.Permission;
using Xunit;

namespace Seeing.Agent.Acp.Tests;

public class AcpPermissionBridgeTests
{
    private static readonly PermissionOption[] Options =
    [
        new PermissionOption { Id = "allow", Label = "允许", Kind = "allow_once" }
    ];

    [Fact]
    public async Task HandleAsync_ShouldPassRequireInteractionAndAgentName()
    {
        var factory = new CapturingPermissionAuthorizerFactory();
        var bridge = new AcpPermissionBridge(factory, NullLogger<AcpPermissionBridge>.Instance);

        using var scope = bridge.Push(new AcpPermissionContext
        {
            SeeingSessionId = "seeing-1",
            LoopId = "loop-9",
            AgentName = "acp-opencode",
            AcpSessionId = "acp-1"
        });

        var response = await bridge.HandleAsync(
            "acp-1",
            new ToolCallUpdate { ToolCallId = "tc-1", ToolName = "read_file" },
            Options);

        response.Outcome.OptionId.Should().Be("allow");
        factory.CapturedSessionId.Should().Be("seeing-1");
        factory.LastRequest.Should().NotBeNull();
        factory.LastRequest!.RequireInteraction.Should().BeTrue();
        factory.LastRequest.AgentName.Should().Be("acp-opencode");
        factory.LastRequest.LoopId.Should().Be("loop-9");
        factory.LastRequest.SessionId.Should().Be("seeing-1");
        factory.LastRequest.Resource.Should().Be("read_file");
        factory.LastRequest.PermissionKind.Should().Be("tool.execute");
    }

    [Fact]
    public async Task HandleAsync_ShouldReturnCancelled_WhenDenied()
    {
        var factory = new CapturingPermissionAuthorizerFactory
        {
            Decision = PermissionEffect.Deny,
            Reason = "用户拒绝"
        };
        var bridge = new AcpPermissionBridge(factory, NullLogger<AcpPermissionBridge>.Instance);

        using var scope = bridge.Push(new AcpPermissionContext
        {
            SeeingSessionId = "seeing-1",
            AgentName = "acp-opencode",
            AcpSessionId = "acp-1"
        });

        var response = await bridge.HandleAsync(
            "acp-1",
            new ToolCallUpdate { ToolCallId = "tc-1", ToolName = "bash" },
            Options);

        response.Outcome.Outcome.Should().Be(PermissionOutcomes.CancelledResponse().Outcome.Outcome);
        factory.LastRequest!.RequireInteraction.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_ShouldThrow_WhenContextNotPushed()
    {
        var factory = new CapturingPermissionAuthorizerFactory();
        var bridge = new AcpPermissionBridge(factory, NullLogger<AcpPermissionBridge>.Instance);

        var act = async () => await bridge.HandleAsync(
            "acp-1",
            new ToolCallUpdate { ToolCallId = "tc-1", ToolName = "bash" },
            Options);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
