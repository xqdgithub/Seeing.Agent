using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Core.Tools;
using Seeing.Agent.Hosting.Commands;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Mcp.Tests;

/// <summary>
/// <c>/mcp list</c> 命令：按 <c>mcp__</c> 前缀识别 MCP 工具。
/// </summary>
public class ToolsCommandsMcpTests
{
    private static McpTool CreateMcpTool(string serverName, string toolName)
    {
        using var document = JsonDocument.Parse("""{"type":"object"}""");
        return new McpTool(
            serverName,
            toolName,
            "MCP 测试工具",
            document.RootElement.Clone(),
            (_, _, _) => Task.FromResult(new McpToolResult { Content = "ok" }));
    }

    private static ToolManager CreateToolManager()
    {
        var hookManager = new HookManager(NullLogger<HookManager>.Instance);
        return new ToolManager(NullLogger<ToolManager>.Instance, hookManager);
    }

    [Fact]
    public void IsMcpToolId_ShouldRecognizeNewPrefixOnly()
    {
        // Arrange & Act & Assert
        ToolsCommands.IsMcpToolId("mcp__docs__search").Should().BeTrue();
        ToolsCommands.IsMcpToolId("MCP__Docs__Search").Should().BeTrue();
        ToolsCommands.IsMcpToolId("mcp_docs_search").Should().BeFalse();
        ToolsCommands.IsMcpToolId("read_file").Should().BeFalse();
        ToolsCommands.IsMcpToolId(null).Should().BeFalse();
    }

    [Fact]
    public async Task ToolsCommands_ManageMcp_ShouldListMcpTools()
    {
        // Arrange：注册一个 MCP 工具与一个内置工具
        var toolManager = CreateToolManager();
        await toolManager.RegisterToolAsync(CreateMcpTool("docs", "search"));
        await toolManager.RegisterToolAsync(new StubTool("read_file"));

        var command = new ToolsCommands(toolManager);

        // Act
        var result = await command.ManageMcp(
            new CommandContext { Arguments = "list" },
            TestContext.Current.CancellationToken);

        // Assert：列表包含 MCP 工具，且不含非 MCP 工具
        result.Success.Should().BeTrue();
        result.Message.Should().Contain("mcp__docs__search");
        result.Message.Should().NotContain("read_file");
        result.Message.Should().NotContain("No MCP tools available");
    }

    /// <summary>最小非 MCP 工具桩。</summary>
    private sealed class StubTool : Seeing.Agent.Abstractions.Tools.ITool
    {
        private static readonly JsonElement s_schema = CreateSchema();

        private static JsonElement CreateSchema()
        {
            using var document = JsonDocument.Parse("""{"type":"object"}""");
            return document.RootElement.Clone();
        }

        public StubTool(string id) => Id = id;

        public string Id { get; }
        public string Description => "桩工具";
        public IReadOnlyList<string> Tags => Array.Empty<string>();
        public Seeing.Agent.Abstractions.Tools.ToolCategory Category =>
            Seeing.Agent.Abstractions.Tools.ToolCategory.General;
        public JsonElement ParametersSchema => s_schema;

        public Task<Seeing.Agent.Abstractions.Tools.ToolResult> ExecuteAsync(
            JsonElement arguments,
            Seeing.Agent.Abstractions.Tools.ToolContext context)
            => Task.FromResult(new Seeing.Agent.Abstractions.Tools.ToolResult { Success = true });
    }
}
