using FluentAssertions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Hosting.Execution.ToolExposure;
using Xunit;

namespace Seeing.Agent.Hosting.Tests.ToolExposure;

public class AllToolExposurePolicyTests
{
    [Fact]
    public void Plan_AllAvailable_ShouldMarkInlineAndHideSearchTool()
    {
        var policy = new AllToolExposurePolicy();
        var input = Input(Entry("a", 10), Entry("tool_search", 30), Entry("b", 20));

        var plan = policy.Plan(input);

        policy.Mode.Should().Be(ToolExposurePolicyMode.All);
        plan.CatalogEntries.Should().HaveCount(3);
        ModeOf(plan, "a").Should().Be(ToolExposureMode.Inline);
        ModeOf(plan, "b").Should().Be(ToolExposureMode.Inline);
        ModeOf(plan, "tool_search").Should().Be(ToolExposureMode.Hidden);
    }

    [Fact]
    public void Plan_ShouldComputeCountsAndTokensExcludingHidden()
    {
        var input = Input(Entry("a", 10), Entry("tool_search", 30), Entry("b", 20));

        var plan = new AllToolExposurePolicy().Plan(input);

        plan.InlineToolCount.Should().Be(2);
        plan.DeferredToolCount.Should().Be(0);
        plan.EstimatedDefinitionTokens.Should().Be(30);
    }

    [Fact]
    public void Plan_ShouldLeaveInlineSchemasEmpty()
    {
        var plan = new AllToolExposurePolicy().Plan(Input(Entry("a", 10)));

        plan.InlineSchemas.Should().BeEmpty();
    }

    private static ToolExposureMode ModeOf(ToolExposurePlan plan, string id) =>
        plan.CatalogEntries.Single(e => e.Id == id).Mode;

    private static ToolCatalogEntry Entry(string id, int tokens = 10) =>
        new(id, null, $"{id}()", "desc", tokens, false, ToolExposureMode.Deferred);

    private static ToolExposureInput Input(params ToolCatalogEntry[] available) =>
        new(
            ToolExposurePolicyMode.All,
            available,
            new HashSet<string>(StringComparer.Ordinal),
            Array.Empty<string>(),
            4000,
            200,
            false);
}
