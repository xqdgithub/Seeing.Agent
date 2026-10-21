using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Prompts;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Hosting.Execution.ToolExposure;
using Seeing.Agent.Hosting.Prompts;
using Xunit;

namespace Seeing.Agent.Hosting.Tests.Prompts;

/// <summary>
/// 组14：工具目录分节——渲染确定性、无延迟不输出、含调用约束与下一轮说明、DI 已注册。
/// </summary>
public class ToolCatalogSectionContributorTests
{
    private static IReadOnlyList<ToolCatalogEntry> Entries() => new[]
    {
        new ToolCatalogEntry("mcp__docs__search", "mcp__docs", "mcp__docs__search(find docs)", "find docs", 40, false, ToolExposureMode.Deferred),
        new ToolCatalogEntry("mcp__fs__read", "mcp__fs", "mcp__fs__read(read file)", "read file", 30, false, ToolExposureMode.Deferred),
    };

    [Fact]
    public void Render_ShouldBeDeterministic()
    {
        var first = ToolCatalogRenderer.Render(Entries());
        var second = ToolCatalogRenderer.Render(Entries());

        first.Should().NotBeNull();
        first.Should().Be(second);
    }

    [Fact]
    public void Render_ShouldIncludeCallConstraintAndNextTurnNote()
    {
        var text = ToolCatalogRenderer.Render(Entries());

        text.Should().NotBeNull();
        text!.Should().Contain("不可直接调用");
        text.Should().Contain("下一轮");
        text.Should().Contain("mcp__docs__search");
    }

    [Fact]
    public void Render_ShouldReturnNull_WhenNoEntries()
    {
        ToolCatalogRenderer.Render(Array.Empty<ToolCatalogEntry>()).Should().BeNull();
    }

    [Fact]
    public async Task Contributor_ShouldNotEmit_WhenNoCatalogSection()
    {
        var contributor = new ToolCatalogSectionContributor();
        var context = new PromptContext();

        var result = await contributor.BuildAsync(context);

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
