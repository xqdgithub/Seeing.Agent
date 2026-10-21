using FluentAssertions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Hosting.Execution.ToolExposure;
using Xunit;

namespace Seeing.Agent.Hosting.Tests.ToolExposure;

public class ToolCatalogBuilderTests
{
    [Fact]
    public void ShouldRetainEveryNamespaceUnderBudget()
    {
        var metas = BuildSixtyToolsAcrossFiveNamespaces();

        // 每行签名固定 13 字符（id 10 + "(d)"），预算 156 恰好容 12 行
        var result = ToolCatalogBuilder.BuildWithinBudget(metas, 156, 200);

        result.Should().HaveCount(12);
        result.Select(e => e.Namespace).Distinct().Should().HaveCount(5);
        foreach (var group in result.GroupBy(e => e.Namespace))
        {
            group.Should().NotBeEmpty();
        }

        var again = ToolCatalogBuilder.BuildWithinBudget(metas, 156, 200);
        result.Should().BeEquivalentTo(again);
    }

    [Fact]
    public void ShouldTruncateDescriptionAtLimit()
    {
        var meta = new ToolMeta("t1", "ns", string.Empty, new string('x', 50), 10, false);

        var entry = ToolCatalogBuilder.Build(new[] { meta }, 10).Single();

        entry.Summary.Should().Be(new string('x', 10) + "…");
        entry.Signature.Should().Be($"t1({entry.Summary})");
        entry.Mode.Should().Be(ToolExposureMode.Deferred);
    }

    [Fact]
    public void ShouldAnnotatePartiallyShownNamespaces()
    {
        var metas = BuildSixtyToolsAcrossFiveNamespaces();

        var result = ToolCatalogBuilder.BuildWithinBudget(metas, 156, 200);

        result.Should().OnlyContain(e => e.Mode == ToolExposureMode.Deferred);
        foreach (var group in result.GroupBy(e => e.Namespace))
        {
            group.First().Summary.Should().Contain("12 tools");
            group.First().Summary.Should().Contain("shown");
        }
    }

    [Fact]
    public void ShouldSortByNamespaceThenId()
    {
        var metas = new[]
        {
            new ToolMeta("z1", "zeta", string.Empty, "d", 1, false),
            new ToolMeta("a2", "alpha", string.Empty, "d", 1, false),
            new ToolMeta("a1", "alpha", string.Empty, "d", 1, false),
            new ToolMeta("z0", "alpha", string.Empty, "d", 1, false)
        };

        var result = ToolCatalogBuilder.Build(metas, 100);

        result.Select(e => e.Id).Should().Equal("a1", "a2", "z0", "z1");
    }

    private static List<ToolMeta> BuildSixtyToolsAcrossFiveNamespaces()
    {
        var metas = new List<ToolMeta>();
        foreach (var ns in new[] { "ns0", "ns1", "ns2", "ns3", "ns4" })
        {
            for (var i = 0; i < 12; i++)
            {
                metas.Add(new ToolMeta($"{ns}_tool{i:D2}", ns, string.Empty, "d", 10, false));
            }
        }

        return metas;
    }
}
