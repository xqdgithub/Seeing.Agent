using Microsoft.Extensions.DependencyInjection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Moq;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Ui;
using Seeing.Agent.Core.Scenarios;
using Seeing.Agent.Mcp;
using Seeing.Agent.Skills;
using Seeing.Agent.WebUI.Services;

namespace Seeing.Agent.WebUI.Tests.Invariants;

/// <summary>
/// P9-T16：UI 贡献不变量 — 侧栏/会话壳分界、未启用友好提示、真相源唯一。
/// </summary>
public class UiContributionTests
{
    private static string RepoRoot => FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Seeing.Agent.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("无法定位仓库根目录（未找到 Seeing.Agent.slnx）");
    }

    private static string WebUiPath(params string[] parts) =>
        Path.Combine([RepoRoot, "samples", "Seeing.Agent.WebUI", .. parts]);

    // ── 1. AppSidebar 无硬编码 MenuItem ──────────────────────────────────────

    [Fact]
    public void AppSidebar_Must_Not_Hardcode_Module_MenuItems()
    {
        var source = File.ReadAllText(WebUiPath("Components", "AppSidebar.razor"));

        source.Should().Contain("Registry.NavItems");
        source.Should().Contain("UiContributionVisibility.FilterSidebarNav");
        source.Should().Contain("IModuleCatalog");

        var hardcoded = new[]
        {
            "NavigateTo(\"/cron-jobs\")",
            "NavigateTo(\"/heartbeat\")",
            "NavigateTo(\"/skills\")",
            "NavigateTo(\"/tools\")",
            "NavigateTo(\"/mcp\")",
            "NavigateTo(\"/acp\")",
            "NavigateTo(\"/memory\")",
            "NavigateTo(\"/gateway\")",
            "NavigateTo(\"/agents\")",
            "Key=\"cron-jobs\"",
            "Key=\"skills\"",
            "Key=\"mcp\"",
        };

        foreach (var needle in hardcoded)
        {
            source.Should().NotContain(needle, $"AppSidebar 不得硬编码模块菜单项: {needle}");
        }

        Regex.Matches(source, @"<MenuItem\b").Count.Should().Be(4,
            "仅允许：1 个聊天壳 MenuItem + 1 个审批中心壳 MenuItem + 1 个未分组 @foreach + 1 个分组 @foreach 模板 MenuItem");
    }

    // ── 2. 未启用模块页面友好提示 ────────────────────────────────────────────

    [Fact]
    public async Task Disabled_Module_DeepLink_Resolves_NotEnabled_With_Friendly_Fragment()
    {
        var registry = new UiContributionRegistry();
        // skills 未 Activate → Routes 无 /skills；已知页仍识别
        var result = ModuleRouteResolver.Resolve("/skills", registry);
        result.Kind.Should().Be(ModuleRouteKind.NotEnabled);
        result.ModuleId.Should().Be("skills");
        result.FeatureTitle.Should().NotBeNullOrWhiteSpace();

        // 已 Activate 但 catalog 禁用 → NotEnabled
        var module = new SkillsModule(registry);
        await module.ActivateAsync(new ServiceCollection().BuildServiceProvider(), TestContext.Current.CancellationToken);
        ModulePageRouteBinder.BindExistingPages(registry);

        var catalog = new Mock<IModuleCatalog>();
        catalog.Setup(c => c.IsEnabled("skills")).Returns(false);

        var disabled = ModuleRouteResolver.Resolve("/skills", registry, catalog.Object);
        disabled.Kind.Should().Be(ModuleRouteKind.NotEnabled);
        disabled.ModuleId.Should().Be("skills");
        disabled.FeatureTitle.Should().Be("技能");

        var router = File.ReadAllText(WebUiPath("Components", "ModuleRouter.razor"));
        router.Should().Contain("NotEnabledFragment");
        router.Should().Contain("ModuleRouteKind.NotEnabled");
        router.Should().NotContain("AppAssembly", "路由唯一源为 registry，不得回退 AppAssembly @page 扫描");

        var fragment = File.ReadAllText(WebUiPath("Components", "NotEnabledFragment.razor"));
        fragment.Should().Contain("当前场景未启用");
        fragment.Should().Contain("切换场景");
        fragment.Should().Contain("返回首页");
    }

    // ── 3. 侧栏只跟 bootEnabled（Requires），不跟进程 Scenario 名 ─────────────

    [Fact]
    public void Sidebar_Follows_BootEnabled_Not_Process_Scenario_Name()
    {
        var catalog = Enabled("memory", "scheduler", "git");
        var nav = new NavContribution[]
        {
            new("/memory", "记忆", "database", ["memory"]),
            new("/cron-jobs", "定时任务", "clock-circle", ["scheduler"]),
            new("/git-only", "Git", "code", ["git"], Scenarios: ["code"]),
        };

        var processFull = UiContributionVisibility.FilterSidebarNav(nav, catalog, "full");
        var processFullAgain = UiContributionVisibility.FilterSidebarNav(nav, catalog, "full");

        // Scenarios 白名单不再硬藏；boot-enabled 的 git 始终可见
        processFull.Select(n => n.Route).Should().BeEquivalentTo("/memory", "/cron-jobs", "/git-only");
        processFullAgain.Select(n => n.Route).Should().Equal(processFull.Select(n => n.Route));

        var processCode = UiContributionVisibility.FilterSidebarNav(nav, catalog, "code");
        processCode.Select(n => n.Route).Should().BeEquivalentTo("/memory", "/cron-jobs", "/git-only");

        var processResearch = UiContributionVisibility.FilterSidebarNav(nav, catalog, "research");
        processResearch.Select(n => n.Route).Should().BeEquivalentTo("/memory", "/cron-jobs", "/git-only");

        // 模块未 boot-enabled 时仍隐藏
        UiContributionVisibility.FilterSidebarNav(nav, Enabled("memory", "scheduler"), "full")
            .Select(n => n.Route).Should().BeEquivalentTo("/memory", "/cron-jobs");
    }

    // ── 4. 会话壳跟会话级；进程 Activate 不变 ────────────────────────────────

    [Fact]
    public async Task SessionShell_Follows_Session_Scenario_Process_Activate_Unchanged()
    {
        const string process = "full";
        var catalog = Enabled("memory", "scheduler", "git");

        // 徽标 / 默认 Agent 随会话场景变
        var badgeBefore = SessionShellVisibility.ResolveEffectiveScenario(null, process);
        var agentBefore = SessionShellVisibility.ResolveDefaultAgent(badgeBefore);
        var badgeAfter = SessionShellVisibility.ResolveEffectiveScenario("research", process);
        var agentAfter = SessionShellVisibility.ResolveDefaultAgent(badgeAfter);

        badgeBefore.Should().Be("full");
        agentBefore.Should().Be(BuiltInScenarios.Full.DefaultAgent);
        badgeAfter.Should().Be("research");
        agentAfter.Should().Be("explore");
        agentBefore.Should().NotBe(agentAfter);

        // 插槽随会话场景变
        var slots = new SlotContribution[]
        {
            new("session_prompt", typeof(object), ["memory"]),
            new("session_prompt", typeof(object), ["git"]),
            new("message_toolbar", typeof(object), ["scheduler"]),
        };

        var codeSlots = SessionShellVisibility.FilterSlotsForSession(
            slots, "session_prompt", catalog, sessionScenario: "code", processScenario: process);
        codeSlots.Should().ContainSingle(s => s.Requires.Contains("git"));
        codeSlots.Should().NotContain(s => s.Requires.Contains("memory"));

        var workSlots = SessionShellVisibility.FilterSlotsForSession(
            slots, "session_prompt", catalog, sessionScenario: "work", processScenario: process);
        workSlots.Should().ContainSingle(s => s.Requires.Contains("memory"));
        workSlots.Should().NotContain(s => s.Requires.Contains("git"));

        // 进程级 Activate 登记不变：切会话场景不触发 Deactivate/注销
        var registry = new UiContributionRegistry();
        var skills = new SkillsModule(registry);
        var mcp = new McpModule(registry);
        await skills.ActivateAsync(new ServiceCollection().BuildServiceProvider(), TestContext.Current.CancellationToken);
        await mcp.ActivateAsync(new ServiceCollection().BuildServiceProvider(), TestContext.Current.CancellationToken);

        var navSnapshot = registry.NavItems.Select(n => n.Route).OrderBy(r => r).ToArray();
        navSnapshot.Should().BeEquivalentTo("/mcp", "/skills");

        // 模拟多次会话场景切换后，registry（Activate 产物）仍完整
        _ = SessionShellVisibility.FilterSlotsForSession(
            slots, "session_prompt", catalog, "code", process);
        _ = SessionShellVisibility.FilterSlotsForSession(
            slots, "session_prompt", catalog, "work", process);
        _ = SessionShellVisibility.ResolveEffectiveScenario("research", process);

        registry.NavItems.Select(n => n.Route).OrderBy(r => r).Should().Equal(navSnapshot);
        registry.Routes.Should().ContainKey("/skills");
        registry.Routes.Should().ContainKey("/mcp");

        // 进程侧栏仍含已启用模块（会话排除不影响）
        var processNav = SessionShellVisibility.FilterNavForProcess(
            [
                new NavContribution("/memory", "记忆", "database", ["memory"]),
                new NavContribution("/cron-jobs", "定时", "clock-circle", ["scheduler"]),
            ],
            catalog,
            process);
        processNav.Select(n => n.Route).Should().BeEquivalentTo("/memory", "/cron-jobs");
    }

    // ── 5. UI 真相源唯一：侧栏只读 IModuleCatalog（bootEnabled）；Scenario 白名单仅会话壳 ─

    [Fact]
    public void Page_Visibility_Single_Source_Of_Truth_Is_Catalog_BootEnabled()
    {
        // 行为：禁用模块 → 侧栏/设置不可见；Scenario 白名单不再硬藏侧栏
        var catalogSkillsOnly = Enabled("skills");
        var nav = new NavContribution[]
        {
            new("/skills", "技能", "star", ["skills"]),
            new("/mcp", "MCP", "api", ["mcp"]),
            new("/memory", "记忆", "database", ["memory"], Scenarios: ["work", "full"]),
            new("/sessions", "会话", "team", []),
        };

        UiContributionVisibility.FilterSidebarNav(nav, catalogSkillsOnly, "full")
            .Select(n => n.Route).Should().BeEquivalentTo("/skills", "/sessions");

        // memory boot-enabled 时，即使 processScenario=code，侧栏仍显示（白名单不生效于侧栏）
        UiContributionVisibility.FilterSidebarNav(nav, Enabled("memory"), "code")
            .Select(n => n.Route).Should().BeEquivalentTo("/memory", "/sessions");

        UiContributionVisibility.FilterSidebarNav(nav, Enabled("memory"), "work")
            .Select(n => n.Route).Should().BeEquivalentTo("/memory", "/sessions");

        var cards = new SettingsCardContribution[]
        {
            new("/settings/scheduler", "调度器", typeof(object), ["scheduler"]),
            new("/settings/demo", "Demo", typeof(object), ["skills"]),
        };
        UiContributionVisibility.FilterSettingsCards(cards, catalogSkillsOnly)
            .Should().ContainSingle(c => c.Route == "/settings/demo");

        // 源码：关键 UI 过滤入口只读 Catalog，无硬编码模块白名单
        var sidebar = File.ReadAllText(WebUiPath("Components", "AppSidebar.razor"));
        sidebar.Should().Contain("Catalog");
        sidebar.Should().Contain("FilterSidebarNav");
        sidebar.Should().NotContain("new[] { \"skills\"");

        var settings = File.ReadAllText(WebUiPath("Pages", "Settings.razor"));
        settings.Should().Contain("FilterSettingsCards");
        settings.Should().Contain("Registry.SettingsCards");
        settings.Should().NotContain("SchedulerStatusPanel");

        var slotHost = File.ReadAllText(WebUiPath("Shared", "SlotHost.razor"));
        slotHost.Should().Contain("IModuleCatalog");
        slotHost.Should().Contain("FilterSlotsForSession");

        var router = File.ReadAllText(WebUiPath("Components", "ModuleRouter.razor"));
        router.Should().Contain("Catalog");
        router.Should().Contain("ModuleRouteResolver.Resolve");
    }

    private static IModuleCatalog Enabled(params string[] ids)
    {
        var set = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
        var mock = new Mock<IModuleCatalog>();
        mock.Setup(c => c.IsEnabled(It.IsAny<string>()))
            .Returns((string id) => set.Contains(id));
        mock.SetupGet(c => c.Enabled).Returns(set);
        return mock.Object;
    }
}
