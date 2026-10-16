using FluentAssertions;
using Moq;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Ui;
using Seeing.Agent.Core.Scenarios;
using Seeing.Agent.WebUI.Services;

namespace Seeing.Agent.WebUI.Tests.Services;

public class SessionShellVisibilityTests
{
    [Fact]
    public void ResolveEffectiveScenario_SessionOverridesProcess()
    {
        SessionShellVisibility.ResolveEffectiveScenario("code", "full").Should().Be("code");
        SessionShellVisibility.ResolveEffectiveScenario(null, "full").Should().Be("full");
        SessionShellVisibility.ResolveEffectiveScenario("  ", "work").Should().Be("work");
    }

    [Fact]
    public void ResolveDefaultAgent_UsesBuiltInScenarioDefaults()
    {
        SessionShellVisibility.ResolveDefaultAgent("full").Should().Be("build");
        SessionShellVisibility.ResolveDefaultAgent("code").Should().BeNull();
        SessionShellVisibility.ResolveDefaultAgent("work").Should().BeNull();
        SessionShellVisibility.ResolveDefaultAgent("research").Should().BeNull();
        SessionShellVisibility.ResolveDefaultAgent("unknown").Should().BeNull();
        SessionShellVisibility.ResolveDefaultAgent("code", NarrowingCatalog()).Should().Be("build");
        SessionShellVisibility.ResolveDefaultAgent("research", NarrowingCatalog()).Should().Be("explore");
    }

    [Fact]
    public void SwitchingSessionScenario_DoesNotChangeProcessNav()
    {
        var catalog = CreateCatalog(enabled: ["memory", "scheduler", "git"]);
        var nav = new[]
        {
            new NavContribution("/memory", "记忆", "database", ["memory"]),
            new NavContribution("/cron-jobs", "定时任务", "clock-circle", ["scheduler"]),
            // Scenarios 白名单不再影响进程侧栏；git boot-enabled 即可见
            new NavContribution("/git-only", "Git", "code", ["git"], Scenarios: ["code"]),
        };

        var processFull = SessionShellVisibility.FilterNavForProcess(nav, catalog, "full");
        var processFullAgain = SessionShellVisibility.FilterNavForProcess(nav, catalog, "full");

        processFull.Select(n => n.Route).Should().BeEquivalentTo("/memory", "/cron-jobs", "/git-only");
        processFullAgain.Select(n => n.Route).Should().Equal(processFull.Select(n => n.Route));

        // 进程 Scenario 名变更也不应隐藏已 boot-enabled 的项
        var processCode = SessionShellVisibility.FilterNavForProcess(nav, catalog, "code");
        processCode.Select(n => n.Route).Should().BeEquivalentTo("/memory", "/cron-jobs", "/git-only");

        var processResearch = SessionShellVisibility.FilterNavForProcess(nav, catalog, "research");
        processResearch.Select(n => n.Route).Should().BeEquivalentTo("/memory", "/cron-jobs", "/git-only");
    }

    [Fact]
    public void FilterSlotsForSession_ExcludesModuleNotInSessionScenario_ProcessPageStillEnabled()
    {
        var catalog = CreateCatalog(enabled: ["memory", "scheduler", "git"]);
        var slots = new[]
        {
            new SlotContribution("session_prompt", typeof(object), ["memory"]),
            new SlotContribution("message_toolbar", typeof(object), ["scheduler"]),
            new SlotContribution("session_prompt", typeof(object), ["git"]),
        };

        // 进程 full 启用 memory/scheduler/git；会话 code 不含 memory → memory 插槽消失
        var codeSlots = SessionShellVisibility.FilterSlotsForSession(
            slots, "session_prompt", catalog, sessionScenario: "code", processScenario: "full", scenarioCatalog: NarrowingCatalog());
        codeSlots.Should().ContainSingle(s => s.Requires.Contains("git"));
        codeSlots.Should().NotContain(s => s.Requires.Contains("memory"));

        // 同进程下会话 work：memory 出现，git 不在 work 模块集 → 消失
        var workSlots = SessionShellVisibility.FilterSlotsForSession(
            slots, "session_prompt", catalog, sessionScenario: "work", processScenario: "full", scenarioCatalog: NarrowingCatalog());
        workSlots.Should().ContainSingle(s => s.Requires.Contains("memory"));
        workSlots.Should().NotContain(s => s.Requires.Contains("git"));

        // 进程级页面可见性（侧栏）仍含 scheduler/memory —— 会话排除不影响
        var processNav = SessionShellVisibility.FilterNavForProcess(
            [
                new NavContribution("/memory", "记忆", "database", ["memory"]),
                new NavContribution("/cron-jobs", "定时", "clock-circle", ["scheduler"]),
            ],
            catalog,
            processScenario: "full");
        processNav.Select(n => n.Route).Should().BeEquivalentTo("/memory", "/cron-jobs");
    }

    [Fact]
    public void FilterSlotsForSession_RespectsContributionScenariosWhitelist()
    {
        var catalog = CreateCatalog(enabled: ["memory"]);
        var slots = new[]
        {
            new SlotContribution(
                "session_prompt",
                typeof(object),
                ["memory"],
                Scenarios: ["work", "research"]),
        };

        SessionShellVisibility.FilterSlotsForSession(
            slots, "session_prompt", catalog, "work", "full", scenarioCatalog: NarrowingCatalog())
            .Should().ContainSingle();

        SessionShellVisibility.FilterSlotsForSession(
            slots, "session_prompt", catalog, "code", "full", scenarioCatalog: NarrowingCatalog())
            .Should().BeEmpty();
    }

    [Fact]
    public void BadgeAndDefaultAgent_ChangeWithSessionScenario()
    {
        const string process = "full";
        var badgeBefore = SessionShellVisibility.ResolveEffectiveScenario(null, process);
        var agentBefore = SessionShellVisibility.ResolveDefaultAgent(badgeBefore);

        var badgeAfter = SessionShellVisibility.ResolveEffectiveScenario("research", process);
        var agentAfter = SessionShellVisibility.ResolveDefaultAgent(badgeAfter, NarrowingCatalog());

        badgeBefore.Should().Be("full");
        agentBefore.Should().Be(BuiltInScenarios.Full.DefaultAgent);
        badgeAfter.Should().Be("research");
        agentAfter.Should().Be("explore");
        agentBefore.Should().NotBe(agentAfter);
    }

    private static IScenarioCatalog NarrowingCatalog()
    {
        var mock = new Mock<IScenarioCatalog>();
        mock.Setup(c => c.Get(It.IsAny<string>())).Returns((string name) => name switch
        {
            "code" => new ScenarioDefinition(
                "code", ["git", "filesystem", "shell", "basic"], "build", new Dictionary<string, string>(), []),
            "work" => new ScenarioDefinition(
                "work", ["memory", "filesystem", "web", "scheduler"], "general", new Dictionary<string, string>(), []),
            "research" => new ScenarioDefinition(
                "research", ["web", "memory"], "explore", new Dictionary<string, string>(), []),
            _ => BuiltInScenarios.TryGet(name),
        });
        return mock.Object;
    }

    private static IModuleCatalog CreateCatalog(IEnumerable<string> enabled)
    {
        var set = new HashSet<string>(enabled, StringComparer.OrdinalIgnoreCase);
        var catalog = new Mock<IModuleCatalog>();
        catalog.Setup(c => c.IsEnabled(It.IsAny<string>()))
            .Returns((string id) => set.Contains(id));
        catalog.SetupGet(c => c.Enabled).Returns(set);
        return catalog.Object;
    }
}
