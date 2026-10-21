using FluentAssertions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Hosting.Execution.ToolExposure;
using Xunit;

namespace Seeing.Agent.Hosting.Tests.ToolExposure;

/// <summary>
/// 目录构建器：行截断、确定性排序、命名空间预算分组（恒含每命名空间与总数、支持 0-shown）。
/// </summary>
public class ToolCatalogBuilderTests
{
    private static ToolMeta Meta(string id, string ns, string desc = "d", int tokens = 10) =>
        new(id, ns, desc, tokens);

    [Fact]
    public void Build_ShouldTruncateDescriptionAtLimit()
    {
        var longDesc = new string('x', 200);
        var entries = ToolCatalogBuilder.Build([Meta("a", "ns", longDesc)], 20);

        var summary = entries.Single().Summary;
        summary.Should().HaveLength(20);       // 含省略号总长不超上限
        summary.Should().EndWith("…");
    }

    [Fact]
    public void Build_ShouldOrderDeterministically()
    {
        var metas = new[] { Meta("z", "b"), Meta("a", "b"), Meta("m", "a") };

        var first = ToolCatalogBuilder.Build(metas, 50);
        var second = ToolCatalogBuilder.Build(metas, 50);

        first.Select(e => e.Id).Should().Equal("m", "a", "z"); // ns 先(a), 再 b 内 id 序 a,z
        first.Should().BeEquivalentTo(second);
    }

    [Fact]
    public void BuildNamespaces_ShouldRetainEveryNamespaceWithTotal()
    {
        // 5 命名空间、各 12 工具，预算仅容约 12 行 → 每命名空间仍出现在分组中并带总数
        var metas = new List<ToolMeta>();
        for (var ns = 0; ns < 5; ns++)
            for (var i = 0; i < 12; i++)
                metas.Add(Meta($"t{ns}_{i}", $"ns{ns}"));

        var deferred = ToolCatalogBuilder.Build(metas, 30);
        var groups = ToolCatalogBuilder.BuildNamespaces(deferred, catalogBudgetChars: 200);

        groups.Should().HaveCount(5);
        groups.Select(g => g.Namespace).Should().OnlyContain(s => s!.StartsWith("ns"));
        groups.Should().OnlyContain(g => g.TotalCount == 12);
    }

    [Fact]
    public void BuildNamespaces_ShouldShowNamespaceEvenWhenZeroFit()
    {
        // 预算 0：每命名空间恒出现且 0 展示（spec MUST：0-shown 仍列）
        var metas = new[] { Meta("a", "x"), Meta("b", "y"), Meta("c", "z") };
        var deferred = ToolCatalogBuilder.Build(metas, 30);

        var groups = ToolCatalogBuilder.BuildNamespaces(deferred, catalogBudgetChars: 0);

        groups.Should().HaveCount(3);
        groups.Should().OnlyContain(g => g.Shown.Count == 0);
        groups.Should().OnlyContain(g => g.TotalCount == 1);
    }

    [Fact]
    public void BuildNamespaces_ShouldBeDeterministic()
    {
        var metas = new[] { Meta("a", "x", "alpha"), Meta("b", "x", "beta"), Meta("c", "y", "gamma") };
        var deferred = ToolCatalogBuilder.Build(metas, 40);

        var first = ToolCatalogBuilder.BuildNamespaces(deferred, 100);
        var second = ToolCatalogBuilder.BuildNamespaces(deferred, 100);

        first.Should().BeEquivalentTo(second);
    }
}
