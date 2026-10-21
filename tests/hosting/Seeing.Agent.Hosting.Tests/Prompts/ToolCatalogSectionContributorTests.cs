using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Prompts;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Hosting.Execution.ToolExposure;
using Seeing.Agent.Hosting.Prompts;
using Xunit;

namespace Seeing.Agent.Hosting.Tests.Prompts;

/// <summary>
/// 组14/17：工具目录分节——渲染确定性、无延迟不输出、含调用约束与下一轮说明、命名空间恒含总数、DI 已注册。
/// </summary>
public class ToolCatalogSectionContributorTests
{
    private static IReadOnlyList<ToolCatalogEntry> Deferred() => new[]
    {
        new ToolCatalogEntry("mcp__docs__search", "mcp__docs", "mcp__docs__search(find docs)", "find docs", 40, false, ToolExposureMode.Deferred),
        new ToolCatalogEntry("mcp__fs__read", "mcp__fs", "mcp__fs__read(read file)", "read file", 30, false, ToolExposureMode.Deferred),
    };

    private static IReadOnlyList<ToolCatalogNamespace> Groups() =>
        ToolCatalogBuilder.BuildNamespaces(Deferred(), catalogBudgetChars: 4000);

    [Fact]
    public void Render_ShouldBeDeterministic()
    {
        var first = ToolCatalogRenderer.Render(Groups(), searchAvailable: true);
        var second = ToolCatalogRenderer.Render(Groups(), searchAvailable: true);

        first.Should().NotBeNull();
        first.Should().Be(second);
    }

    [Fact]
    public void Render_ShouldIncludeCallConstraintAndNextTurnNote()
    {
        var text = ToolCatalogRenderer.Render(Groups(), searchAvailable: true);

        text.Should().NotBeNull();
        text!.Should().Contain("不可直接调用");
        text.Should().Contain("下一轮");
        text.Should().Contain("mcp__docs__search");
    }

    [Fact]
    public void Render_ShouldEnumerateNamespacesWithCounts()
    {
        // spec：命名空间行 MUST 含总数；已展示 MUST 标注数量
        var text = ToolCatalogRenderer.Render(Groups(), searchAvailable: true);

        text.Should().NotBeNull();
        text!.Should().Contain("mcp__docs (1 tools, 1 shown)");
        text.Should().Contain("mcp__fs (1 tools, 1 shown)");
    }

    [Fact]
    public void Render_ShouldNotPointToToolSearch_WhenUnavailable()
    {
        var text = ToolCatalogRenderer.Render(Groups(), searchAvailable: false);

        text.Should().NotBeNull();
        text!.Should().NotContain("先用 tool_search 检索");
    }

    [Fact]
    public void Render_ShouldReturnNull_WhenNoGroups()
    {
        ToolCatalogRenderer.Render(Array.Empty<ToolCatalogNamespace>(), searchAvailable: true).Should().BeNull();
    }

    [Fact]
    public void Render_ShouldNotContainPermissionDecision()
    {
        var text = ToolCatalogRenderer.Render(Groups(), searchAvailable: true);

        text.Should().NotBeNull();
        text!.Should().NotContain("允许");
        text.Should().NotContain("拒绝");
        text.Should().NotContain("审批");
        var lower = text!.ToLowerInvariant();
        lower.Should().NotContain("permission");
        lower.Should().NotContain("deny");
        lower.Should().NotContain("approve");
    }

    [Fact]
    public async Task Contributor_ShouldNotEmit_WhenNoCatalogSection()
    {
        var contributor = new ToolCatalogSectionContributor();

        var result = await contributor.BuildAsync(new PromptContext());

        result.Should().BeNull();
    }

    [Fact]
    public async Task Contributor_ShouldEmitCatalogText_WhenSectionPresent()
    {
        var contributor = new ToolCatalogSectionContributor();
        var context = new PromptContext
        {
            Sections = new Dictionary<string, string> { [ToolCatalogSectionContributor.CatalogSectionKey] = "CATALOG-BODY" }
        };

        var result = await contributor.BuildAsync(context);

        result.Should().Be("CATALOG-BODY");
        contributor.Heading.Should().Be("## Tool Catalog");
    }

    [Fact]
    public void ShouldBeRegisteredInDI()
    {
        var services = new ServiceCollection();
        services.AddExecutionEngine();

        var contributors = services
            .BuildServiceProvider()
            .GetServices<IPromptSectionContributor>()
            .ToList();

        contributors.Should().Contain(c => c.GetType() == typeof(ToolCatalogSectionContributor));
    }
}
