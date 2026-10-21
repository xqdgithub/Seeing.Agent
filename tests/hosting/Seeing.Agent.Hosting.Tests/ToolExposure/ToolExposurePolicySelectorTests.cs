using FluentAssertions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Hosting.Execution.ToolExposure;
using Xunit;

namespace Seeing.Agent.Hosting.Tests.ToolExposure;

public class ToolExposurePolicySelectorTests
{
    [Fact]
    public void Select_AllMode_ShouldReturnAllPolicy()
    {
        var selector = BuildSelector();

        selector.Select(ToolExposurePolicyMode.All).Should().BeOfType<AllToolExposurePolicy>();
    }

    [Fact]
    public void Select_AutoAndTieredModes_ShouldBothReturnTieredPolicy()
    {
        var selector = BuildSelector();

        selector.Select(ToolExposurePolicyMode.Auto).Should().BeOfType<TieredToolExposurePolicy>();
        selector.Select(ToolExposurePolicyMode.Tiered).Should().BeOfType<TieredToolExposurePolicy>();
    }

    [Fact]
    public void Select_ModeWithoutRegistration_ShouldThrow()
    {
        var selector = new ToolExposurePolicySelector(new IToolExposurePolicy[] { new AllToolExposurePolicy() });

        var act = () => selector.Select(ToolExposurePolicyMode.Tiered);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Constructor_DuplicateModeRegistration_ShouldThrow()
    {
        var policies = new IToolExposurePolicy[] { new AllToolExposurePolicy(), new AllToolExposurePolicy() };

        var act = () => new ToolExposurePolicySelector(policies);

        act.Should().Throw<InvalidOperationException>();
    }

    private static ToolExposurePolicySelector BuildSelector() =>
        new(new IToolExposurePolicy[] { new AllToolExposurePolicy(), new TieredToolExposurePolicy() });
}
