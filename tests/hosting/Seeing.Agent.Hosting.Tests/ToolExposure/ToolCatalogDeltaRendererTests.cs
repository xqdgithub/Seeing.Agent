using FluentAssertions;
using Seeing.Agent.Hosting.Execution.ToolExposure;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Hosting.Tests.ToolExposure;

public class ToolCatalogDeltaRendererTests
{
    private static JsonElement Canonical(params (string Ns, string[] Ids)[] groups) =>
        JsonSerializer.SerializeToElement(new
        {
            namespaces = groups.Select(g => new { name = g.Ns, tools = g.Ids }).ToList()
        });

    [Fact]
    public void 新增工具_应叙述()
    {
        var text = ToolCatalogDeltaRenderer.Render(
            Canonical(("mcp__docs", ["a"])),
            Canonical(("mcp__docs", ["a", "b"])));

        text.Should().Contain("mcp__docs");
        text.Should().Contain("+1");
        text.Should().Contain("b");
    }

    [Fact]
    public void 移除工具_应叙述并含移除措辞()
    {
        var text = ToolCatalogDeltaRenderer.Render(
            Canonical(("mcp__docs", ["a", "b"])),
            Canonical(("mcp__docs", ["a"])));

        text.Should().Contain("mcp__docs");
        text.Should().Contain("-1");
        text.Should().Contain("b");
        text.Should().Contain(ToolCatalogDeltaRenderer.RemovedPhrase);
    }

    [Fact]
    public void 新命名空间_应完整叙述()
    {
        var text = ToolCatalogDeltaRenderer.Render(
            Canonical(("通用", ["read"])),
            Canonical(("通用", ["read"]), ("mcp__docs", ["x", "y"])));

        text.Should().Contain("mcp__docs");
        text.Should().Contain("x");
        text.Should().Contain("y");
    }

    [Fact]
    public void 无变化_应返回空串()
    {
        var canonical = Canonical(("通用", ["read"]));

        ToolCatalogDeltaRenderer.Render(canonical, canonical).Should().BeEmpty();
    }

    [Fact]
    public void 同一输入_应逐字节相等()
    {
        var previous = Canonical(("通用", ["read"]));
        var current = Canonical(("通用", ["read", "write"]));

        ToolCatalogDeltaRenderer.Render(previous, current)
            .Should().Be(ToolCatalogDeltaRenderer.Render(previous, current));
    }

    [Fact]
    public void 前值为空_应将全部工具视为新增()
    {
        var text = ToolCatalogDeltaRenderer.Render(null, Canonical(("通用", ["read", "write"])));

        text.Should().Contain("+2");
        text.Should().Contain("read");
        text.Should().Contain("write");
    }
}
