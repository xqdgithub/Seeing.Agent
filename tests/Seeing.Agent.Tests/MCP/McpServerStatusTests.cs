using FluentAssertions;
using Seeing.Agent.MCP;
using Seeing.Agent.MCP.Core;
using Xunit;

namespace Seeing.Agent.Tests.MCP;

public class McpServerStatusTests
{
    [Fact]
    public void IsAvailable_WhenConnectedAndNotDisabled_ShouldBeTrue()
    {
        // Arrange
        var status = CreateStatus(disabled: false, state: McpConnectionState.Connected);

        // Act & Assert
        status.IsAvailable.Should().BeTrue();
        status.IsDisabled.Should().BeFalse();
        status.State.Should().Be(McpConnectionState.Connected);
    }

    [Fact]
    public void IsAvailable_WhenDisabledState_ShouldBeFalse()
    {
        // Arrange
        var status = CreateStatus(disabled: true, state: McpConnectionState.Disabled);

        // Act & Assert
        status.IsAvailable.Should().BeFalse();
        status.IsDisabled.Should().BeTrue();
    }

    [Fact]
    public void CanReconnect_WhenDisabledAndError_ShouldBeFalse()
    {
        // Arrange
        var status = CreateStatus(disabled: true, state: McpConnectionState.Error);

        // Act & Assert
        status.CanReconnect.Should().BeFalse();
    }

    [Fact]
    public void CanReconnect_WhenEnabledAndError_ShouldBeTrue()
    {
        // Arrange
        var status = CreateStatus(disabled: false, state: McpConnectionState.Error);

        // Act & Assert
        status.CanReconnect.Should().BeTrue();
    }

    private static McpServerStatus CreateStatus(bool disabled, McpConnectionState state)
    {
        var config = new McpServerConfig
        {
            Name = "test",
            Command = "npx",
            Disabled = disabled
        };

        return McpServerStatusBuilder.From(McpServerStatus.Create("test", config))
            .WithState(state)
            .Build();
    }
}
