using FluentAssertions;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Mcp;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.MCP;
using Seeing.Agent.MCP.Configuration;
using Seeing.Agent.MCP.Factory;
using Seeing.Agent.MCP.Policy;
using Seeing.Agent.Tools;
using System.Net.Http;
using Xunit;
using CoreMcpConnectionState = Seeing.Agent.Abstractions.Mcp.McpConnectionState;

namespace Seeing.Agent.Tests.MCP;

public class McpClientManagerReinitializeTests
{
    [Fact]
    public async Task InitializeAsync_重置后重新初始化_应重新加载配置并新建连接()
    {
        // Arrange: 假工厂统计包装器创建次数，两个启用服务器各提供 2 个工具
        var factory = new FakeWrapperFactory { ToolCount = 2 };
        var manager = CreateManager(factory);
        var configs = CreateConfigs("srv_a", "srv_b");

        // Act: 首次初始化并等待连接就绪
        await manager.InitializeAsync(configs);
        (await manager.WaitForReadyAsync("srv_a")).Should().BeTrue();
        (await manager.WaitForReadyAsync("srv_b")).Should().BeTrue();

        manager.GetAllConfigs().Count.Should().Be(2);
        manager.GetAllStatus().Count.Should().Be(2);
        var createdAfterFirst = factory.CreatedCount;

        // 重置全部 MCP 状态
        await manager.ResetAllAsync();

        // Assert: 重置后配置与状态清空
        manager.GetAllConfigs().Count.Should().Be(0);
        manager.GetAllStatus().Count.Should().Be(0);

        // 重置后再次初始化
        await manager.InitializeAsync(configs);
        (await manager.WaitForReadyAsync("srv_a")).Should().BeTrue();
        (await manager.WaitForReadyAsync("srv_b")).Should().BeTrue();

        // Assert: 配置与状态重新加载，且创建了全新连接实例
        manager.GetAllConfigs().Count.Should().Be(2);
        manager.GetAllStatus().Count.Should().Be(2);
        factory.CreatedCount.Should().BeGreaterThan(createdAfterFirst);
        manager.GetStatus("srv_a")!.State.Should().Be(CoreMcpConnectionState.Connected);
    }

    [Fact]
    public async Task InitializeAsync_重复调用_不重复注册服务器与工具()
    {
        // Arrange: 两个启用服务器各提供 2 个工具
        var factory = new FakeWrapperFactory { ToolCount = 2 };
        var manager = CreateManager(factory);
        var configs = CreateConfigs("srv_a", "srv_b");

        // Act: 首次初始化并等待连接就绪
        await manager.InitializeAsync(configs);
        (await manager.WaitForReadyAsync("srv_a")).Should().BeTrue();
        (await manager.WaitForReadyAsync("srv_b")).Should().BeTrue();

        var toolsAfterFirst = manager.GetTools().Count;
        toolsAfterFirst.Should().Be(4);

        // 第二次初始化（同一配置）
        await manager.InitializeAsync(configs);
        (await manager.WaitForReadyAsync("srv_a")).Should().BeTrue();
        (await manager.WaitForReadyAsync("srv_b")).Should().BeTrue();

        // Assert: 服务器与工具均不翻倍
        manager.GetAllConfigs().Count.Should().Be(2);
        manager.GetAllStatus().Count.Should().Be(2);
        manager.GetTools().Count.Should().Be(toolsAfterFirst);
    }

    [Fact]
    public async Task ResetAllAsync_未初始化时_应安全执行无副作用()
    {
        // Arrange: 全新 manager，从未初始化
        var manager = CreateManager(new FakeWrapperFactory());

        // Act: 直接重置
        await manager.ResetAllAsync();

        // Assert: 无异常，状态保持为空
        manager.GetAllConfigs().Count.Should().Be(0);
        manager.GetAllStatus().Count.Should().Be(0);
    }

    private static McpClientManager CreateManager(FakeWrapperFactory factory)
    {
        var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning));
        var hookManager = new HookManager(loggerFactory.CreateLogger<HookManager>());
        var toolManager = new ToolManager(
            loggerFactory.CreateLogger<ToolManager>(),
            hookManager);
        var factoryRegistry = new McpWrapperFactoryRegistry();
        factoryRegistry.Register(factory);
        var configPersistence = new Moq.Mock<IMcpConfigPersistence>().Object;
        return new McpClientManager(
            loggerFactory.CreateLogger<McpClientManager>(),
            loggerFactory,
            hookManager,
            toolManager,
            factoryRegistry,
            new McpGlobalPolicy(),
            configPersistence);
    }

    private static Dictionary<string, McpServerConfig> CreateConfigs(params string[] names)
    {
        var configs = new Dictionary<string, McpServerConfig>();
        foreach (var name in names)
        {
            configs[name] = new McpServerConfig { Command = "npx" };
        }
        return configs;
    }

    private sealed class FakeWrapperFactory : IMcpClientWrapperFactory
    {
        public McpTransportType TransportType => McpTransportType.Stdio;

        public int ToolCount { get; init; } = 2;

        public int CreatedCount => _createdCount;
        private int _createdCount;

        public IMcpClientWrapper Create(
            McpServerConfig config,
            IHttpClientFactory? httpClientFactory,
            ILoggerFactory loggerFactory)
        {
            Interlocked.Increment(ref _createdCount);
            return new FakeWrapper(ToolCount);
        }
    }

    private sealed class FakeWrapper : IMcpClientWrapper
    {
        private readonly int _toolCount;

        public FakeWrapper(int toolCount) => _toolCount = toolCount;

        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DisconnectAsync() => Task.CompletedTask;

        public Task<IReadOnlyList<Seeing.Agent.MCP.Management.McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default)
        {
            var tools = Enumerable.Range(1, _toolCount)
                .Select(i => new Seeing.Agent.MCP.Management.McpToolInfo { Name = $"tool_{i}", Description = "测试工具" })
                .ToList();
            return Task.FromResult<IReadOnlyList<Seeing.Agent.MCP.Management.McpToolInfo>>(tools);
        }

        public Task<McpToolResult> CallToolAsync(
            string toolName,
            Dictionary<string, object?> args,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new McpToolResult { IsError = false, Content = "ok" });
    }
}
