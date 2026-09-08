using FluentAssertions;
using Seeing.Agent.Abstractions.Ui;
using Xunit;

namespace Seeing.Agent.Tests.Ui;

public class UiContributionContractsTests
{
    [Fact]
    public void NavContribution_ShouldConstructWithRequiredFields()
    {
        var contribution = new NavContribution(
            "/memory",
            "Memory",
            "database",
            ["memory"],
            ["work", "research"]);

        contribution.Route.Should().Be("/memory");
        contribution.Title.Should().Be("Memory");
        contribution.Icon.Should().Be("database");
        contribution.Requires.Should().Equal("memory");
        contribution.Scenarios.Should().Equal("work", "research");
    }

    [Fact]
    public void NavContribution_ScenariosShouldDefaultToNull()
    {
        var contribution = new NavContribution("/cron-jobs", "Cron Jobs", "clock", ["scheduler"]);

        contribution.Scenarios.Should().BeNull();
    }

    [Fact]
    public void SettingsCardContribution_ShouldCarryComponentType()
    {
        var contribution = new SettingsCardContribution(
            "/settings/memory",
            "Memory",
            typeof(FakeSettingsComponent),
            ["memory"]);

        contribution.Route.Should().Be("/settings/memory");
        contribution.Title.Should().Be("Memory");
        contribution.ComponentType.Should().Be(typeof(FakeSettingsComponent));
        contribution.Requires.Should().Equal("memory");
    }

    [Fact]
    public void SlotContribution_ShouldConstructWithOptionalScenarios()
    {
        var contribution = new SlotContribution(
            "session_prompt",
            typeof(FakeSlotComponent),
            ["memory"],
            ["work"]);

        contribution.Name.Should().Be("session_prompt");
        contribution.ComponentType.Should().Be(typeof(FakeSlotComponent));
        contribution.Requires.Should().Equal("memory");
        contribution.Scenarios.Should().Equal("work");
    }

    [Fact]
    public void MessageRendererContribution_ShouldCarryMessageTypeAndComponentType()
    {
        var contribution = new MessageRendererContribution("task", typeof(FakeMessageRenderer));

        contribution.MessageType.Should().Be("task");
        contribution.ComponentType.Should().Be(typeof(FakeMessageRenderer));
    }

    [Fact]
    public void IUiContribution_ShouldExposeModuleIdAndContribute()
    {
        typeof(IUiContribution).GetProperty(nameof(IUiContribution.ModuleId))
            .Should().NotBeNull()
            .And.Subject!.PropertyType.Should().Be(typeof(string));

        var method = typeof(IUiContribution).GetMethod(nameof(IUiContribution.Contribute));
        method.Should().NotBeNull();
        method!.ReturnType.Should().Be(typeof(IReadOnlyList<object>));
        method.GetParameters().Should().BeEmpty();
    }

    [Fact]
    public void IUiContributionRegistry_ShouldExposeCollectionsAndRegister()
    {
        var registry = typeof(IUiContributionRegistry);

        registry.GetMethod(nameof(IUiContributionRegistry.Register))
            .Should().NotBeNull()
            .And.Subject!.GetParameters().Should().ContainSingle()
            .Which.ParameterType.Should().Be(typeof(IUiContribution));

        registry.GetProperty(nameof(IUiContributionRegistry.NavItems))!
            .PropertyType.Should().Be(typeof(IReadOnlyList<NavContribution>));

        registry.GetProperty(nameof(IUiContributionRegistry.SettingsCards))!
            .PropertyType.Should().Be(typeof(IReadOnlyList<SettingsCardContribution>));

        registry.GetProperty(nameof(IUiContributionRegistry.Slots))!
            .PropertyType.Should().Be(typeof(IReadOnlyList<SlotContribution>));

        registry.GetProperty(nameof(IUiContributionRegistry.MessageRenderers))!
            .PropertyType.Should().Be(typeof(IReadOnlyList<MessageRendererContribution>));

        registry.GetProperty(nameof(IUiContributionRegistry.Routes))!
            .PropertyType.Should().Be(typeof(IReadOnlyDictionary<string, NavContribution>));
    }

    private sealed class FakeSettingsComponent;

    private sealed class FakeSlotComponent;

    private sealed class FakeMessageRenderer;
}
