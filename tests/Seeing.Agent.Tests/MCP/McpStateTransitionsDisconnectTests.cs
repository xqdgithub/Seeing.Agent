using Seeing.Agent.Abstractions.Mcp;
using Seeing.Agent.MCP.Core;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Seeing.Agent.Configuration;
using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.MCP;
using Seeing.Agent.MCP.Configuration;
using Seeing.Agent.Abstractions.Mcp;
using Seeing.Agent.MCP.Core;
using Seeing.Agent.MCP.Factory;
using Seeing.Agent.MCP.Policy;
using Seeing.Agent.Tools;
using Xunit;
using CoreMcpConnectionState = Seeing.Agent.Abstractions.Mcp.McpConnectionState;

namespace Seeing.Agent.Tests.MCP;

public class McpStateTransitionsDisconnectTests
{
    [Fact]
    public void StateTransitions_ErrorToPending_ShouldBeAllowed()
    {
        McpStateTransitions.CanTransition(CoreMcpConnectionState.Error, CoreMcpConnectionState.Pending)
            .Should().BeTrue();
    }

    [Fact]
    public void StateTransitions_ConnectingToPending_ShouldBeAllowed()
    {
        McpStateTransitions.CanTransition(CoreMcpConnectionState.Connecting, CoreMcpConnectionState.Pending)
            .Should().BeTrue();
    }

    [Fact]
    public async Task DisconnectServer_WhenError_ShouldReturnToPending()
    {
        var manager = await CreateConnectedToErrorAsync("broken");

        var result = await manager.DisconnectServerAsync("broken");

        result.Success.Should().BeTrue();
        manager.GetStatus("broken")!.State.Should().Be(CoreMcpConnectionState.Pending);
    }

    [Fact]
    public async Task RemoveServer_WhenError_ShouldSucceed()
    {
        var manager = await CreateConnectedToErrorAsync("broken");

        var result = await manager.RemoveServerAsync("broken", persist: false);

        result.Success.Should().BeTrue();
        manager.GetStatus("broken").Should().BeNull();
        manager.GetConfig("broken").Should().BeNull();
    }

    [Fact]
    public async Task RemoveServer_WhenConnecting_ShouldSucceed()
    {
        var manager = await CreateConnectedToErrorAsync("connecting-srv");

        var connectingStatus = McpServerStatusBuilder.From(manager.GetStatus("connecting-srv")!)
            .WithState(CoreMcpConnectionState.Connecting)
            .Build();
        manager.UpdateState("connecting-srv", connectingStatus);

        var result = await manager.RemoveServerAsync("connecting-srv", persist: false);

        result.Success.Should().BeTrue();
        manager.GetStatus("connecting-srv").Should().BeNull();
        manager.GetConfig("connecting-srv").Should().BeNull();
    }

    /// <summary>
    /// 以 Disabled 添加避免 AutoStart 后台连接抢锁，再手动 Connect 进入 Error。
    /// </summary>
    private static async Task<McpClientManager> CreateConnectedToErrorAsync(string name)
    {
        var manager = CreateManager();
        await manager.AddServerAsync(
            name,
            new McpServerConfig { Command = "nonexistent-mcp-server-xyz", Disabled = true },
            persist: false);

        manager.GetConfig(name)!.Disabled = false;
        await manager.ConnectServerAsync(name);
        manager.GetStatus(name)!.State.Should().Be(CoreMcpConnectionState.Error);
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
        factoryRegistry.Register(new Seeing.Agent.MCP.Factory.StdioWrapperFactory());
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
