using FluentAssertions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Hosting.Execution.ToolExposure;
using Xunit;

namespace Seeing.Agent.Hosting.Tests.ToolExposure;

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

        plan.CatalogEntries.Should().HaveCount(1);
        plan.CatalogEntries[0].Id.Should().Be("o1");
        plan.CatalogEntries[0].Mode.Should().Be(ToolExposureMode.Deferred);
        plan.InlineToolCount.Should().Be(3);
        plan.DeferredToolCount.Should().Be(1);
        plan.InlineSchemas.Should().BeEmpty();
    }

    [Fact]
    public void Plan_DiscoveredIdNoLongerAvailable_ShouldBeDropped()
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
    public void Plan_Escalated_ShouldTurnAllDeferredIntoInlineKeepingSearchToolInline()
    {
        var input = Input(
            new[] { Entry("a", 1), Entry("tool_search", 2), Entry("b", 3) },
            escalated: true);

        var plan = new TieredToolExposurePolicy().Plan(input);

        plan.CatalogEntries.Should().BeEmpty();
        plan.InlineToolCount.Should().Be(3);
        plan.DeferredToolCount.Should().Be(0);
        plan.EstimatedDefinitionTokens.Should().Be(6);
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
