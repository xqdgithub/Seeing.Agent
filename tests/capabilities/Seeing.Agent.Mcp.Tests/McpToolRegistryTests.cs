using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Mcp.Management;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Mcp.Tests;

/// <summary>
/// MCP 工具注册表：同名 server 抢名拒绝与同连接重连保持 id 不变。
/// </summary>
public class McpToolRegistryTests
{
    /// <summary>构造注册表与可断言的工具管理器 Mock。</summary>
    private static (McpToolRegistry Registry, Mock<IToolManager> ToolManager) CreateRegistry()
    {
        var hookManager = new Mock<IHookManager>();
        hookManager
            .Setup(h => h.TriggerBlockingAsync(
                It.IsAny<HookSpec>(),
                It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, object?>>(),
                It.IsAny<IDictionary<string, object?>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(HookResult.Success);

        var toolManager = new Mock<IToolManager>();
        toolManager
            .Setup(t => t.RegisterToolAsync(It.IsAny<ITool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        toolManager
            .Setup(t => t.UnregisterTool(It.IsAny<string>()))
            .Returns(true);

        var registry = new McpToolRegistry(
            toolManager.Object,
            hookManager.Object,
            NullLogger<McpToolRegistry>.Instance);

        return (registry, toolManager);
    }

    private static McpToolInfo CreateToolInfo(string name)
    {
        using var document = JsonDocument.Parse("{}");
        return new McpToolInfo
        {
            Name = name,
            Description = "测试工具",
            ParametersSchema = document.RootElement.Clone()
        };
    }

    [Fact]
    public async Task DuplicateServerName_ShouldThrow()
    {
        // Arrange：两个不同连接（不同 owner 实例）以同一 server 名注册
        var (registry, _) = CreateRegistry();
        var connectionA = new object();
        var connectionB = new object();
        var toolId = McpTool.BuildId("docs", "search");

        await registry.RegisterToolAsync(
            "docs", toolId, CreateToolInfo("search"), connectionA, TestContext.Current.CancellationToken);

        // Act
        var act = () => registry.RegisterToolAsync(
            "docs", toolId, CreateToolInfo("search"), connectionB, TestContext.Current.CancellationToken);

        // Assert：明确报错，且已注册工具不受影响
        await act.Should().ThrowAsync<McpDuplicateServerException>()
            .WithMessage("*docs*重复注册*");
        registry.ContainsTool(toolId).Should().BeTrue();
        registry.GetToolCount("docs").Should().Be(1);
    }

    [Fact]
    public async Task Reconnect_SameServer_ShouldKeepId()
    {
        // Arrange：同一连接（同一 owner 引用）断开后重连并重新提供同名工具
        var (registry, toolManager) = CreateRegistry();
        var connection = new object();
        var toolId = McpTool.BuildId("docs", "search");

        await registry.RegisterToolAsync(
            "docs", toolId, CreateToolInfo("search"), connection, TestContext.Current.CancellationToken);

        await registry.UnregisterToolAsync("docs", toolId, TestContext.Current.CancellationToken);

        // Act：重连后以同一 owner 再次注册
        await registry.RegisterToolAsync(
            "docs", toolId, CreateToolInfo("search"), connection, TestContext.Current.CancellationToken);

        // Assert
        registry.ContainsTool(toolId).Should().BeTrue();
        registry.GetServerToolIds("docs").Should().BeEquivalentTo([toolId]);
        toolManager.Verify(t => t.RegisterToolAsync(
            It.Is<ITool>(x => x.Id == "mcp__docs__search"), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Reconnect_WithoutUnregister_ShouldKeepId()
    {
        // Arrange：同连接未注销即重新注册（重连竞态下可能发生）
        var (registry, _) = CreateRegistry();
        var connection = new object();
        var toolId = McpTool.BuildId("docs", "search");

        await registry.RegisterToolAsync(
            "docs", toolId, CreateToolInfo("search"), connection, TestContext.Current.CancellationToken);

        // Act
        var act = () => registry.RegisterToolAsync(
            "docs", toolId, CreateToolInfo("search"), connection, TestContext.Current.CancellationToken);

        // Assert
        await act.Should().NotThrowAsync();
        registry.GetServerToolIds("docs").Should().BeEquivalentTo([toolId]);
    }

    [Fact]
    public async Task RegisterTool_SameOwnerDuplicateId_ShouldOverwriteWithoutThrowing()
    {
        // Arrange：同一 toolId 注册两次（同一连接 = 重连语义）
        var (registry, toolManager) = CreateRegistry();
        var connection = new object();
        var toolId = McpTool.BuildId("docs", "search");

        await registry.RegisterToolAsync(
            "docs", toolId, CreateToolInfo("search"), connection, TestContext.Current.CancellationToken);

        // Act
        var act = () => registry.RegisterToolAsync(
            "docs", toolId, CreateToolInfo("search"), connection, TestContext.Current.CancellationToken);

        // Assert：重连允许覆盖，与 ToolManager 对重复 id 的覆盖语义一致
        await act.Should().NotThrowAsync();
        registry.GetToolCount("docs").Should().Be(1);
        toolManager.Verify(t => t.RegisterToolAsync(
            It.Is<ITool>(x => x.Id == "mcp__docs__search"), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task UnregisterAll_ShouldReleaseServerNameForNewConnection()
    {
        // Arrange：确认 server 移除后，新连接可用同名重新注册（不视为重复）
        var (registry, _) = CreateRegistry();
        var connectionA = new object();
        var connectionB = new object();
        var toolId = McpTool.BuildId("docs", "search");

        await registry.RegisterToolAsync(
            "docs", toolId, CreateToolInfo("search"), connectionA, TestContext.Current.CancellationToken);
        await registry.UnregisterAllToolsAsync("docs");

        // Act
        var act = () => registry.RegisterToolAsync(
            "docs", toolId, CreateToolInfo("search"), connectionB, TestContext.Current.CancellationToken);

        // Assert
        await act.Should().NotThrowAsync();
        registry.ContainsTool(toolId).Should().BeTrue();
    }
}
