using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Seeing.Agent.Abstractions.Mcp;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Core.Tools;
using Seeing.Agent.Mcp;
using Seeing.Agent.Mcp.Configuration;
using Seeing.Agent.Mcp.Core;
using Seeing.Agent.Mcp.Factory;
using Seeing.Agent.Mcp.Policy;
using Xunit;
using CoreMcpConnectionState = Seeing.Agent.Abstractions.Mcp.McpConnectionState;

namespace Seeing.Agent.Tests.MCP;

/// <summary>
/// 回归：Pause 不得先经 Disconnect 落到 Pending 再转 Paused（会抛无效状态转换并冲崩 WebUI）。
/// </summary>
public class McpPauseStateTransitionTests
{
    [Fact]
    public void StateTransitions_PendingToPaused_ShouldBeAllowed()
    {
        McpStateTransitions.CanTransition(CoreMcpConnectionState.Pending, CoreMcpConnectionState.Paused)
            .Should().BeTrue();
    }

    [Fact]
    public void StateTransitions_ConnectedToPaused_ShouldBeAllowed()
    {
        McpStateTransitions.CanTransition(CoreMcpConnectionState.Connected, CoreMcpConnectionState.Paused)
            .Should().BeTrue();
    }

    [Fact]
    public async Task PauseServer_WhenConnected_ShouldLandOnPaused_WithoutThrowing()
    {
        var manager = await CreateWithCoordinatorAsync("pause-connected");

        var connected = McpServerStatusBuilder.From(manager.GetStatus("pause-connected")!)
            .WithState(CoreMcpConnectionState.Connected)
            .WithToolCount(0)
            .WithToolNames(Array.Empty<string>())
            .Build();
        manager.UpdateState("pause-connected", connected);

        var result = await manager.PauseServerAsync("pause-connected", TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        manager.GetStatus("pause-connected")!.State.Should().Be(CoreMcpConnectionState.Paused);
    }

    [Fact]
    public async Task PauseServer_WhenPending_ShouldLandOnPaused_WithoutThrowing()
    {
        var manager = await CreateWithCoordinatorAsync("pause-pending");

        var disconnect = await manager.DisconnectServerAsync("pause-pending", TestContext.Current.CancellationToken);
        disconnect.Success.Should().BeTrue();
        manager.GetStatus("pause-pending")!.State.Should().Be(CoreMcpConnectionState.Pending);

        var result = await manager.PauseServerAsync("pause-pending", TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        manager.GetStatus("pause-pending")!.State.Should().Be(CoreMcpConnectionState.Paused);
    }

    /// <summary>
    /// 以 Disabled 添加避免 AutoStart，再 Connect 创建 coordinator（命令不存在会落到 Error）。
    /// </summary>
    private static async Task<McpClientManager> CreateWithCoordinatorAsync(string name)
    {
        var manager = CreateManager();
        await manager.AddServerAsync(
            name,
            new McpServerConfig { Command = "nonexistent-mcp-server-xyz", Disabled = true },
            persist: false);

        manager.GetConfig(name)!.Disabled = false;
        await manager.ConnectServerAsync(name, TestContext.Current.CancellationToken);
        manager.GetStatus(name).Should().NotBeNull();
        return manager;
    }

    private static McpClientManager CreateManager()
    {
        var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning));
        var hookManager = new HookManager(loggerFactory.CreateLogger<HookManager>());
        var toolManager = new ToolManager(
            loggerFactory.CreateLogger<ToolManager>(),
            hookManager);
        var factoryRegistry = new McpWrapperFactoryRegistry();
        factoryRegistry.Register(new StdioWrapperFactory());
        var configPersistence = new Mock<IMcpConfigPersistence>().Object;
        return new McpClientManager(
            loggerFactory.CreateLogger<McpClientManager>(),
            loggerFactory,
            hookManager,
            toolManager,
            factoryRegistry,
            new McpGlobalPolicy(),
            configPersistence);
    }
}
