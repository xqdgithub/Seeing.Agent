using FluentAssertions;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.WebUI.Components.Models;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Components.Models;

/// <summary>
/// 模型页 Provider 列表的分组与排序规则守门。
/// <para>背景：原先按 Provider id 字典序排列，而列表显示的是 <c>Name</c>；
/// 配置型与扩展混排，看起来毫无规律（用户实际反馈「感觉好乱」）。</para>
/// </summary>
public class ProviderListGroupingTests
{
    [Fact]
    public void Build_ShouldOrderGroups_ConfiguredThenBuiltinThenExtension()
    {
        var groups = ProviderListGrouping.Build(Providers(
            ("trip", "trip", ProviderOrigin.Configured),
            ("deepseek", "DeepSeek", ProviderOrigin.Extension),
            ("legacy", "Legacy", ProviderOrigin.Builtin)));

        groups.Select(g => g.Key).Should().Equal("configured", "builtin", "extension");
        groups.Select(g => g.Title).Should().Equal("我的配置", "内置", "扩展");
    }

    [Fact]
    public void Build_ShouldSortWithinGroup_ByDisplayName_NotById()
    {
        // id 序 = aaa, bbb, ccc；Name 序 = Zulu, Alpha, Mike
        var groups = ProviderListGrouping.Build(
            Providers(
                ("aaa", "Zulu", ProviderOrigin.Extension),
                ("bbb", "Alpha", ProviderOrigin.Extension),
                ("ccc", "Mike", ProviderOrigin.Extension)),
            StringComparer.OrdinalIgnoreCase);

        groups.Should().ContainSingle();
        groups[0].Items.Select(p => p.Value.Name)
            .Should().Equal(
                ["Alpha", "Mike", "Zulu"],
                "顺序由用户看得见的 Name 决定，而不是 id");
    }

    [Fact]
    public void Build_ShouldFallBackToId_WhenNameMissing()
    {
        var groups = ProviderListGrouping.Build(
            Providers(
                ("beta", null, ProviderOrigin.Configured),
                ("alpha", null, ProviderOrigin.Configured)),
            StringComparer.OrdinalIgnoreCase);

        groups[0].Items.Select(p => p.Key).Should().Equal("alpha", "beta");
    }

    [Fact]
    public void Build_ShouldOmitEmptyGroups()
    {
        var groups = ProviderListGrouping.Build(
            Providers(("deepseek", "DeepSeek", ProviderOrigin.Extension)));

        groups.Should().ContainSingle();
        groups[0].Key.Should().Be("extension",
            "只有扩展时才不该出现「我的配置」「内置」这两个空标题");
    }

    [Fact]
    public void Build_ShouldReturnEmpty_WhenNoProviders()
    {
        ProviderListGrouping.Build(new Dictionary<string, ProviderInfo>()).Should().BeEmpty();
    }

    private static Dictionary<string, ProviderInfo> Providers(
        params (string Id, string? Name, ProviderOrigin Origin)[] entries)
        => entries.ToDictionary(
            e => e.Id,
            e => new ProviderInfo { Id = e.Id, Name = e.Name, Origin = e.Origin },
            StringComparer.OrdinalIgnoreCase);
}
