using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Prompts;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Hosting.Prompts;
using Xunit;

namespace Seeing.Agent.Hosting.Tests.Prompts;

/// <summary>
/// 工具目录分节贡献者：无内容不输出、有内容透传、Heading 自述、DI 已注册。
/// （目录文本的生成与换行/计数规则由结算引擎及其测试覆盖。）
/// </summary>
public class ToolCatalogSectionContributorTests
{
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
            Sections = new Dictionary<string, string>
            {
                [ToolCatalogSectionContributor.CatalogSectionKey] = "CATALOG-BODY"
            }
        };

        var result = await contributor.BuildAsync(context);

        result.Should().Be("CATALOG-BODY");
        contributor.Heading.Should().Be("## Tool Catalog");
        contributor.SectionName.Should().Be(PromptSectionNames.ToolCatalog);
    }

    [Fact]
    public void ShouldBeRegisteredInDI()
    {
        var services = new ServiceCollection();
        services.AddExecutionEngine();

        var contributors = services.BuildServiceProvider().GetServices<IPromptSectionContributor>().ToList();

        contributors.Should().Contain(c => c.GetType() == typeof(ToolCatalogSectionContributor));
    }
}
