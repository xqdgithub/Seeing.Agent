using FluentAssertions;
using Seeing.Agent.Abstractions.Ui;
using Seeing.Agent.Core.Modules;
using Seeing.Agent.Core.Ui;
using Seeing.Agent.WebUI.Services;
using Seeing.Agent.WebUI.Shell;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Shell;

/// <summary>
/// 页面通道迁移的完整性与等价性守门（决策 D10）。
/// <para>
/// 迁移把路由声明从 <c>ModulePageRouteBinder</c>（宿主侧 15 条硬编码表 + enrich 覆盖）
/// 拆到「模块自己声明 + 宿主声明壳层」。这里用<b>真实的</b>模块与宿主贡献者组装一次注册表，
/// 钉住三件事：
/// </para>
/// <list type="number">
/// <item>28 条路由（15 模块 + 13 壳层）一条不少——缺任一条直链就掉 Fallback。</item>
/// <item>5 条曾被 enrich 补全的详情路由由模块自己声明，且 <c>Nav == null</c>（不进侧栏）。</item>
/// <item>所有贡献都能通过宿主的视图校验（视图表与声明没有漂移）。</item>
/// </list>
/// </summary>
public class PageChannelMigrationTests
{
    private const string HostOwnerId = "host.webui";

    private static UiPoint PagePoint => WebUiPoints.ShellPage;

    /// <summary>迁移前 15 条模块路由（逐行照抄 <c>ModulePageRouteBinder.KnownModuleRoutes</c>）。</summary>
    private static readonly (string ModuleId, string Route, string Title, string Icon, bool InSidebar)[] s_legacyModuleRoutes =
    [
        ("memory", "/memory", "记忆", "database", true),
        ("memory", "/memory/graph", "知识图谱", "apartment", false),
        ("memory", "/memory/detail/{*FilePath}", "记忆详情", "file", false),
        ("scheduler", "/cron-jobs", "定时任务", "clock-circle", true),
        ("scheduler", "/cron-jobs/{JobId}", "任务详情", "clock-circle", false),
        ("scheduler", "/heartbeat", "心跳", "heart", true),
        ("mcp", "/mcp", "MCP", "api", true),
        ("acp", "/acp", "ACP", "robot", true),
        ("gateway", "/gateway", "Gateway", "global", true),
        ("gateway", "/gateway-clients", "Gateway 客户端", "api", true),
        ("skills", "/skills", "技能", "star", true),
        ("skills", "/skills/create", "创建技能", "star", false),
        ("skills", "/skills/{SkillName}", "技能详情", "star", false),
        ("basic", "/tools", "工具", "tool", true),
        ("reflection", "/reflection", "复盘记录", "bulb", true),
    ];

    /// <summary>迁移前 13 条壳层路由（7 条纯路由 + 6 条侧栏 Nav）。</summary>
    private static readonly (string Route, string Title, bool InSidebar)[] s_legacyShellRoutes =
    [
        ("/", "首页", false),
        ("/home", "首页", false),
        ("/workspaces", "选择工作区", false),
        ("/session", "会话", false),
        ("/session/{SessionId}", "会话", false),
        ("/conference", "会议视图", false),
        ("/conference/{SessionId}", "会议视图", false),
        ("/sessions", "会话", true),
        ("/agents", "智能体管理", true),
        ("/agent-config", "智能体配置", true),
        ("/models", "模型", true),
        ("/security", "安全", true),
        ("/settings", "系统设置", true),
    ];

    [Fact]
    public void AllDeclarations_ShouldPassHostValidation_WithoutDiagnostics()
    {
        var registry = BuildRegistry();

        registry.Diagnostics.Should().BeEmpty(
            "任何被拒收的贡献都会让对应页面/设置卡静默消失；诊断内容："
            + string.Join("; ", registry.Diagnostics.Select(d => $"{d.Kind}:{d.OwnerId}/{d.ContributionId}:{d.Message}")));
    }

    [Fact]
    public void MigratedRouteSet_ShouldCoverAllLegacyRoutes()
    {
        var declared = BuildRegistry().Pages.Select(p => p.Route).ToArray();

        declared.Should().BeEquivalentTo(
            s_legacyModuleRoutes.Select(r => r.Route).Concat(s_legacyShellRoutes.Select(r => r.Route)),
            "迁移后必须完整覆盖 15 + 13 条路由，缺任一条都会让直链掉 Fallback");
    }

    [Fact]
    public void TheFiveEnrichOnlyRoutes_MustBeDeclaredByModulesWithNullNav()
    {
        // 这 5 条在旧实现里只由 ModulePageRouteBinder 的 enrich 产生（模块自己没声明）。
        // 它们都是已知子路径，因此必须 Nav == null（不进侧栏）。
        string[] enrichOnly =
        [
            "/memory/graph",
            "/memory/detail/{*FilePath}",
            "/cron-jobs/{JobId}",
            "/skills/create",
            "/skills/{SkillName}",
        ];

        var registry = BuildRegistry();

        foreach (var route in enrichOnly)
        {
            var page = registry.Pages.Should().ContainSingle(p => p.Route == route,
                $"'{route}' 必须由模块显式声明，否则迁移后直链 404").Subject;

            page.Nav.Should().BeNull($"'{route}' 是子路径，不该出现在侧栏");
            page.Requires.Should().HaveCount(1, "模块贡献必须带自身归属");
            page.ViewKey.Should().Be(page.Id, "ViewKey 约定与 Id 相同，便于守门");
        }
    }

    [Fact]
    public void ModuleContributions_ShouldAllDeclareOwnership()
    {
        var registry = BuildRegistry();

        var moduleEntries = registry.All
            .Where(e => !string.Equals(e.OwnerId, HostOwnerId, StringComparison.Ordinal))
            .ToList();

        moduleEntries.Should().NotBeEmpty();
        foreach (var entry in moduleEntries)
        {
            entry.Contribution.Requires.Should().Equal(
                new[] { entry.OwnerId },
                $"'{entry.Contribution.Id}' 的贡献必须声明归属自身模块（空 Requires 会被注册表拒收）");
        }
    }

    [Fact]
    public void HostContributions_ShouldBeProcessLevel()
    {
        var hostEntries = BuildRegistry().All
            .Where(e => string.Equals(e.OwnerId, HostOwnerId, StringComparison.Ordinal))
            .ToList();

        hostEntries.Should().HaveCount(18, "壳层是 13 条页面 + 5 张设置卡");

        // 设置卡「代模块声明」的三张必须带归属（scheduler / memory / browser）；
        // 其余壳层贡献是进程级（Requires 为空），否则用户在切换 Boot 能力集后会失去入口。
        hostEntries
            .Where(e => e.Contribution is not SettingsCardContribution)
            .Should().OnlyContain(e => e.Contribution.Requires.Count == 0,
                "壳层页面必须始终可达，不能被模块启停门控");
    }

    [Fact]
    public void SidebarMembership_ShouldMatchLegacy()
    {
        var inSidebar = VisiblePages(BuildRegistry())
            .Where(p => p.Nav is not null)
            .Select(p => p.Route)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var expected = s_legacyModuleRoutes.Where(r => r.InSidebar).Select(r => r.Route)
            .Concat(s_legacyShellRoutes.Where(r => r.InSidebar).Select(r => r.Route))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        inSidebar.Should().BeEquivalentTo(expected,
            "侧栏项必须与迁移前逐项一致（参数化路由与已知子路径不进侧栏）");
    }

    [Fact]
    public void Sidebar_ShouldKeepThreeGroupsInLegacyOrder()
    {
        var projection = SidebarProjectionService.Project(
            VisibleEntries(BuildRegistry()),
            WebUiIconMap.Resolve);

        // 控制中心 → 工作区 → 设置（迁移前是硬编码的 preferred 数组，现由 GroupOrder 表达）
        projection.Groups.Select(g => g.Title).Should().Equal(
            NavGroups.Control, NavGroups.Workspace, NavGroups.Settings);

        projection.Ungrouped.Should().BeEmpty("迁移前的所有侧栏项都在分组内");
    }

    [Fact]
    public void SidebarItems_ShouldKeepLegacyTitlesAndIcons()
    {
        var projection = SidebarProjectionService.Project(
            VisibleEntries(BuildRegistry()),
            WebUiIconMap.Resolve);

        var actual = projection.Visible
            .ToDictionary(v => v.Route, StringComparer.OrdinalIgnoreCase);

        foreach (var legacy in s_legacyModuleRoutes.Where(r => r.InSidebar))
        {
            actual.Should().ContainKey(legacy.Route);
            actual[legacy.Route].Title.Should().Be(legacy.Title,
                $"'{legacy.Route}' 的标题必须与迁移前一致");
            actual[legacy.Route].Icon.Should().Be(legacy.Icon,
                $"'{legacy.Route}' 的图标必须与迁移前一致");
        }

        foreach (var legacy in s_legacyShellRoutes.Where(r => r.InSidebar))
        {
            actual.Should().ContainKey(legacy.Route);
            actual[legacy.Route].Title.Should().Be(legacy.Title,
                $"'{legacy.Route}' 的标题必须与迁移前一致");
        }
    }

    [Fact]
    public void DetailAndShellParameterRoutes_ShouldResolveByUrl()
    {
        var registry = BuildRegistry();
        var entries = registry.All;

        (string Path, string ExpectedId)[] cases =
        [
            ("/memory/graph", "page.memory.graph"),
            ("/memory/detail/daily/2026-11-10/a.md", "page.memory.detail"),
            ("/cron-jobs/job_abc", "page.cron.detail"),
            ("/skills/create", "page.skills.create"),
            ("/skills/my-skill", "page.skills.detail"),
            ("/", "page.home"),
            ("/home", "page.home.classic"),
            ("/session/abc123", "page.session.detail"),
            ("/conference/abc123", "page.conference.detail"),
            ("/gateway-clients", "page.gateway.clients"),
        ];

        foreach (var (path, expectedId) in cases)
        {
            var match = UiRouteTemplate.Resolve(path, entries, IsVisible);

            match.Should().NotBeNull($"'{path}' 必须能由路由表解析");
            match!.Page.Id.Should().Be(expectedId, $"'{path}' 应解析到 {expectedId}");
        }
    }

    [Fact]
    public void EveryPageViewKey_ShouldBeResolvable()
    {
        var views = new WebUiViewResolver();
        WebUiViews.RegisterBuiltIns(views);

        foreach (var page in BuildRegistry().Pages)
            views.CanResolve(page.ViewKey).Should().BeTrue($"页面 '{page.Id}' 的视图键必须已登记");
    }

    [Fact]
    public void DisabledModuleRoute_ShouldStillResolveToKnownPageTitle()
    {
        // 迁移前靠 ModulePageRouteBinder.TryMatchKnown 给「未启用」提示带出正确标题。
        // 现在必须由「声明全集」兜底：停用模块的声明仍在表里（声明早于激活），
        // 只是可见性谓词把它滤掉，因此不能直接落 Fallback —— 否则用户看到
        // 「未启用『页面』」这种没有任何信息的文案。
        var registry = BuildRegistry();
        var context = UiVisibilityContext.Process(new MemoryDisabledCatalog());

        var visibleMatch = UiRouteTemplate.Resolve(
            "/memory/graph", registry.All, e => e.Contribution is PageContribution p
                                                 && UiVisibility.IsVisible(p, PagePoint, context));

        visibleMatch.Should().BeNull("memory 被禁用，可见集里不该有这个页面");

        var knownMatch = UiRouteTemplate.Resolve("/memory/graph", registry.All);
        knownMatch.Should().NotBeNull("声明全集里必须有它，供「未启用」提示取标题");
        knownMatch!.Page.Title.Should().Be("知识图谱");
        knownMatch.Page.Requires.Should().Equal("memory");
    }

    // ── helpers ──

    /// <summary>
    /// 可见性用「全部模块已启用」的进程语义：本组测试关心的是声明齐全与侧栏映射，
    /// 模块启用状态由结算决定，与这里无关（真实的停用态由 <c>UiVisibilityTests</c> 覆盖）。
    /// </summary>
    private static bool IsVisible(UiContributionEntry entry)
        => UiVisibility.IsVisible(
            entry.Contribution, PagePoint, UiVisibilityContext.Process(new AllEnabledCatalog()));

    private static IReadOnlyList<PageContribution> VisiblePages(UiContributionRegistry registry)
        => registry.All.Where(IsVisible).Select(e => e.Contribution).OfType<PageContribution>().ToList();

    private static IReadOnlyList<UiContributionEntry> VisibleEntries(UiContributionRegistry registry)
        => registry.All.Where(IsVisible).ToList();

    /// <summary>「所有模块都启用」的模块目录替身（避免为一条可见性断言造一整套结算）。</summary>
    private sealed class AllEnabledCatalog : Seeing.Agent.Abstractions.Modules.IModuleCatalog
    {
        public IReadOnlyCollection<Seeing.Agent.Abstractions.Modules.ModuleDescriptor> Available => [];

        public IReadOnlyCollection<string> Enabled => [];

        public bool IsEnabled(string id) => true;

        public bool IsAvailable(string id) => true;

        public IReadOnlyDictionary<string, string?> Unhealthy => new Dictionary<string, string?>();
    }

    /// <summary>仅 <c>memory</c> 停用的模块目录替身。</summary>
    private sealed class MemoryDisabledCatalog : Seeing.Agent.Abstractions.Modules.IModuleCatalog
    {
        public IReadOnlyCollection<Seeing.Agent.Abstractions.Modules.ModuleDescriptor> Available => [];

        public IReadOnlyCollection<string> Enabled => [];

        public bool IsEnabled(string id) => !string.Equals(id, "memory", StringComparison.OrdinalIgnoreCase);

        public bool IsAvailable(string id) => true;

        public IReadOnlyDictionary<string, string?> Unhealthy => new Dictionary<string, string?>();
    }

    private static UiContributionRegistry BuildRegistry()
    {
        var views = new WebUiViewResolver();
        WebUiViews.RegisterBuiltIns(views);

        var registry = new UiContributionRegistry(
            new UiPointCatalog(WebUiPoints.All),
            [new WebUiUiContributionValidator(views)]);

        var host = new WebUiHostContributor();
        registry.AddRange(host.OwnerId, host.DescribeUi());

        foreach (var module in AllUiModules())
            registry.AddRange(module.Id, ((IUiContributor)module).DescribeUi());

        return registry;
    }

    /// <summary>
    /// 声明 UI 的 8 个能力模块（与迁移前 <c>KnownModuleRoutes</c> 的模块集合一致）。
    /// 全部走无依赖构造函数：<c>DescribeUi()</c> 是纯数据，不触达任何宿主依赖。
    /// </summary>
    private static IEnumerable<Seeing.Agent.Abstractions.Modules.ISeeingModule> AllUiModules()
    {
        yield return new Seeing.Agent.Tools.Basic.BasicModule();
        yield return new Seeing.Agent.Skills.SkillsModule();
        yield return new Seeing.Agent.Mcp.McpModule();
        yield return new Seeing.Agent.Scheduler.SchedulerModule();
        yield return new Seeing.Agent.Memory.MemoryModule();
        yield return new Seeing.Agent.Acp.AcpModule();
        yield return new Seeing.Agent.Reflection.ReflectionModule();
        yield return new Seeing.Agent.Gateway.GatewayModule();
    }
}
