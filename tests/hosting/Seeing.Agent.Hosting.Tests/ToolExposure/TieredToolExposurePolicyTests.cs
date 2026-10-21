using FluentAssertions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Hosting.Execution.ToolExposure;
using Xunit;

namespace Seeing.Agent.Hosting.Tests.ToolExposure;

/// <summary>
/// Tiered 分层策略：pinned/已发现/tool_search 内联，其余延迟。
/// 升级(auto→分层)由 resolver 依阈值决定，本策略对 Escalated 参数不敏感（纯分层语义）。
/// 契约：<c>Plan.CatalogEntries</c> 返回全量条目并各带最终 Mode（与 All 策略一致）。
/// </summary>
public class TieredToolExposurePolicyTests
{
    [Fact]
    public void Plan_PinnedAndDiscoveredAndSearchTool_ShouldStayInlineOthersDeferred()
    {
        var input = Input(
            new[] { Entry("p1"), Entry("d1"), Entry("tool_search"), Entry("o1") },
            discovered: new HashSet<string>(StringComparer.Ordinal) { "d1" },
            pinned: new[] { "p1" });

        var plan = new TieredToolExposurePolicy().Plan(input);

        // 全量条目带 Mode：4 条
        plan.CatalogEntries.Should().HaveCount(4);
        plan.CatalogEntries.Single(e => e.Id == "o1").Mode.Should().Be(ToolExposureMode.Deferred);
        plan.CatalogEntries.Where(e => e.Mode == ToolExposureMode.Inline).Select(e => e.Id)
            .Should().BeEquivalentTo(["p1", "d1", "tool_search"]);
        plan.InlineToolCount.Should().Be(3);
        plan.DeferredToolCount.Should().Be(1);
        plan.InlineSchemas.Should().BeEmpty();
        // 定义 token 仅计内联（3 条各 10）
        plan.EstimatedDefinitionTokens.Should().Be(30);
    }

    [Fact]
    public void Plan_DiscoveredIdNoLongerAvailable_ShouldNotAppear()
    {
        var input = Input(
            new[] { Entry("a") },
            discovered: new HashSet<string>(StringComparer.Ordinal) { "gone" });

        var plan = new TieredToolExposurePolicy().Plan(input);

        plan.CatalogEntries.Select(e => e.Id).Should().NotContain("gone");
        plan.InlineToolCount.Should().Be(0);
        plan.DeferredToolCount.Should().Be(1);
    }

    [Fact]
    public void Plan_EscalatedFlag_ShouldStillDeferNonPinned()
    {
        // 升级由 resolver 选择策略实现；Tiered 本身对 Escalated 无副作用（不退化为全内联）。
        var input = Input(
            new[] { Entry("a", 1), Entry("tool_search", 2), Entry("b", 3) },
            escalated: true);

        var plan = new TieredToolExposurePolicy().Plan(input);

        plan.CatalogEntries.Should().HaveCount(3);
        plan.CatalogEntries.Single(e => e.Id == "tool_search").Mode.Should().Be(ToolExposureMode.Inline);
        plan.CatalogEntries.Single(e => e.Id == "a").Mode.Should().Be(ToolExposureMode.Deferred);
        plan.InlineToolCount.Should().Be(1);
        plan.DeferredToolCount.Should().Be(2);
        plan.EstimatedDefinitionTokens.Should().Be(2);
    }

    [Fact]
    public void Plan_SameInputTwice_ShouldProduceEqualPlans()
    {
        var input = Input(
            new[] { Entry("p1"), Entry("d1"), Entry("tool_search"), Entry("o1") },
            discovered: new HashSet<string>(StringComparer.Ordinal) { "d1" },
            pinned: new[] { "p1" });
        var policy = new TieredToolExposurePolicy();

        var first = policy.Plan(input);
        var second = policy.Plan(input);

        first.Should().BeEquivalentTo(second);
    }

    private static ToolCatalogEntry Entry(string id, int tokens = 10) =>
        new(id, null, $"{id}()", "desc", tokens, false, ToolExposureMode.Deferred);

    private static ToolExposureInput Input(
        IReadOnlyList<ToolCatalogEntry> available,
        IReadOnlySet<string>? discovered = null,
        IReadOnlyList<string>? pinned = null,
        bool escalated = false) =>
        new(
            ToolExposurePolicyMode.Tiered,
            available,
            discovered ?? new HashSet<string>(StringComparer.Ordinal),
            pinned ?? Array.Empty<string>(),
            4000,
            200,
            escalated);
}
