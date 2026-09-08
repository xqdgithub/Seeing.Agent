using FluentAssertions;
using Moq;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Ui;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Modules;
using Seeing.Agent.WebUI.Services;

namespace Seeing.Agent.WebUI.Tests.Services;

public class UiContributionVisibilityTests
{
    [Fact]
    public void FilterSidebarNav_ExcludesParameterizedAndChildRoutes()
    {
        var catalog = Enabled("skills", "memory");
        var nav = new NavContribution[]
        {
            new("/skills", "技能", "star", ["skills"]),
            new("/skills/{SkillName}", "技能详情", "star", ["skills"]),
            new("/skills/create", "创建技能", "star", ["skills"]),
            new("/memory", "记忆", "database", ["memory"]),
            new("/memory/settings", "记忆设置", "setting", ["memory"]),
            new("/gateway-clients", "Gateway 客户端", "api", ["gateway"]),
        };

        var visible = UiContributionVisibility.FilterSidebarNav(nav, catalog, processScenario: "full");

        visible.Select(n => n.Route).Should().BeEquivalentTo("/skills", "/memory");
    }

    [Fact]
    public void FilterSidebarNav_RequiresEnabledModules()
    {
        var catalog = Enabled("skills");
        var nav = new NavContribution[]
        {
            new("/skills", "技能", "star", ["skills"]),
            new("/mcp", "MCP", "api", ["mcp"]),
            new("/sessions", "会话", "team", []),
        };

        var visible = UiContributionVisibility.FilterSidebarNav(nav, catalog, "full");

        visible.Select(n => n.Route).Should().BeEquivalentTo("/skills", "/sessions");
    }

    [Fact]
    public void FilterSidebarNav_ScenariosWhitelist_UsesProcessScenarioOnly()
    {
        var catalog = Enabled("memory");
        var nav = new NavContribution[]
        {
            new("/memory", "记忆", "database", ["memory"], Scenarios: ["work", "full"]),
            new("/sessions", "会话", "team", []),
        };

        UiContributionVisibility.FilterSidebarNav(nav, catalog, "code")
            .Select(n => n.Route).Should().Equal("/sessions");

        UiContributionVisibility.FilterSidebarNav(nav, catalog, "work")
            .Select(n => n.Route).Should().BeEquivalentTo("/memory", "/sessions");
    }

    [Fact]
    public void FilterSettingsCards_HidesWhenModuleDisabled()
    {
        var catalog = Enabled("skills");
        var cards = new SettingsCardContribution[]
        {
            new("/settings/scheduler", "调度器", typeof(object), ["scheduler"]),
            new("/settings/demo", "Demo", typeof(object), ["skills"]),
        };

        var visible = UiContributionVisibility.FilterSettingsCards(cards, catalog);

        visible.Should().ContainSingle(c => c.Route == "/settings/demo");
    }

    [Fact]
    public void ResolveProcessScenario_PrefersOptionsThenHostDefault()
    {
        UiContributionVisibility.ResolveProcessScenario(
                new SeeingAgentOptions { Scenario = "work" },
                new ProcessSettlementOptions { HostDefaultScenario = "full" })
            .Should().Be("work");

        UiContributionVisibility.ResolveProcessScenario(
                new SeeingAgentOptions { Scenario = null },
                new ProcessSettlementOptions { HostDefaultScenario = "full" })
            .Should().Be("full");
    }

    private static IModuleCatalog Enabled(params string[] ids)
    {
        var mock = new Mock<IModuleCatalog>();
        mock.Setup(c => c.IsEnabled(It.IsAny<string>()))
            .Returns((string id) => ids.Contains(id, StringComparer.OrdinalIgnoreCase));
        return mock.Object;
    }
}
