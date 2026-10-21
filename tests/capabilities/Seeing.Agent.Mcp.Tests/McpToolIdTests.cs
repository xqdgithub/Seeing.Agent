using FluentAssertions;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Mcp.Tests;

/// <summary>
/// MCP 工具 id 命名：统一为 <c>mcp__{server}__{tool}</c>，消除下划线拼接歧义。
/// </summary>
public class McpToolIdTests
{
    private static McpTool CreateTool(string serverName, string toolName)
    {
        using var document = JsonDocument.Parse("{}");
        return new McpTool(
            serverName,
            toolName,
            "测试工具",
            document.RootElement.Clone(),
            (_, _, _) => Task.FromResult(new McpToolResult { Content = "ok" }));
    }

    [Fact]
    public void McpTool_Id_ShouldUseDoubleUnderscoreForm()
    {
        // Arrange & Act
        var tool = CreateTool("docs", "search");

        // Assert
        tool.Id.Should().Be("mcp__docs__search");
    }

    [Fact]
    public void McpTool_Ids_ShouldNotCollideForUnderscoreAmbiguity()
    {
        // Arrange & Act：server a_b 的工具 c 与 server a 的工具 b_c
        var first = CreateTool("a_b", "c");
        var second = CreateTool("a", "b_c");

        // Assert：旧形式 a_b_c 会撞车，新形式可区分
        first.Id.Should().Be("mcp__a_b__c");
        second.Id.Should().Be("mcp__a__b_c");
        first.Id.Should().NotBe(second.Id);
    }

    [Fact]
    public void McpTool_Namespace_ShouldBeServerPrefixed()
    {
        // Arrange & Act
        var tool = CreateTool("docs", "search");

        // Assert
        tool.Namespace.Should().Be("mcp__docs");
        tool.Id.Should().StartWith(tool.Namespace);
    }

    [Fact]
    public void BuildId_ShouldMatchInstanceId()
    {
        // Arrange & Act
        var tool = CreateTool("docs", "search");

        // Assert：注册表键与工具适配器 id 必须同源
        McpTool.BuildId("docs", "search").Should().Be(tool.Id);
    }
}
