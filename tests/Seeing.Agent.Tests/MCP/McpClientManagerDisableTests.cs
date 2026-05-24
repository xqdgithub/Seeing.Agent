using FluentAssertions;
using Microsoft.Extensions.Logging;
using Seeing.Agent.MCP;
using Seeing.Agent.MCP.Core;
using Xunit;
using CoreMcpConnectionState = Seeing.Agent.MCP.Core.McpConnectionState;

namespace Seeing.Agent.Tests.MCP;

public class McpClientManagerDisableTests
{
    [Fact]
    public async Task AddServer_Disabled_ShouldStartDisabledAndUnavailable()
    {
        // Arrange
        var manager = CreateManager();

        // Act
        var result = await manager.AddServerAsync(
            "filesystem",
            new McpServerConfig { Command = "npx", Disabled = true },
            persist: false);

        // Assert
        result.Success.Should().BeTrue();
        var status = manager.GetStatus("filesystem");
        status.Should().NotBeNull();
        status!.State.Should().Be(CoreMcpConnectionState.Disabled);
        status.IsDisabled.Should().BeTrue();
        status.IsAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task DisableServer_WhenConnected_ShouldBeDisabledAndUnavailable()
    {
        // Arrange
        var manager = CreateManager();
        await manager.AddServerAsync(
            "srv",
            new McpServerConfig { Command = "npx" },
            persist: false);

        var connected = McpServerStatusBuilder.From(manager.GetStatus("srv")!)
            .WithConnected()
            .WithToolCount(2)
            .WithToolNames(["tool_a", "tool_b"])
            .Build();
        manager.UpdateState("srv", connected);

        // Act
        var result = await manager.DisableServerAsync("srv", persist: false);

        // Assert
        result.Success.Should().BeTrue();
        var status = manager.GetStatus("srv");
        status!.IsDisabled.Should().BeTrue();
        status.IsAvailable.Should().BeFalse();
        status.State.Should().Be(CoreMcpConnectionState.Disabled);
        status.ToolCount.Should().Be(0);
        manager.IsAvailable("srv").Should().BeFalse();
    }

    [Fact]
    public async Task EnableServer_AfterDisabled_ShouldTransitionToConnecting()
    {
        // Arrange
        var manager = CreateManager();
        await manager.AddServerAsync(
            "time",
            new McpServerConfig { Command = "npx", Disabled = true },
            persist: false);

        // Act
        var result = await manager.EnableServerAsync("time", persist: false);

        // Assert
        result.Success.Should().BeTrue();
        result.Error.Should().BeNull();
        result.Status.Should().Be(CoreMcpConnectionState.Connecting);

        var status = manager.GetStatus("time");
        status!.IsDisabled.Should().BeFalse();
        status.State.Should().Be(CoreMcpConnectionState.Connecting);
        manager.GetConfig("time")!.Disabled.Should().BeFalse();
    }

    [Fact]
    public void StateTransitions_DisabledToConnecting_ShouldBeAllowed()
    {
        McpStateTransitions.CanTransition(CoreMcpConnectionState.Disabled, CoreMcpConnectionState.Connecting)
            .Should().BeTrue();
    }

    [Fact]
    public void StateTransitions_PendingToDisabled_ShouldBeAllowed()
    {
        McpStateTransitions.CanTransition(CoreMcpConnectionState.Pending, CoreMcpConnectionState.Disabled)
            .Should().BeTrue();
    }

    private static McpClientManager CreateManager()
    {
        var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning));
        return new McpClientManager(
            loggerFactory.CreateLogger<McpClientManager>(),
            loggerFactory);
    }
}
