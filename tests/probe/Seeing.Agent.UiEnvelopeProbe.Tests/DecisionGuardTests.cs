using FluentAssertions;
using Seeing.Agent.UiEnvelopeProbe;

namespace Seeing.Agent.UiEnvelopeProbe.Tests;

/// <summary>
/// 决策 D10/D11/D12 的守门测试。
/// </summary>
public class DecisionGuardTests
{
    private static readonly UiPoint PagePoint = new()
    { Id = "shell.page", DisplayName = "页面", PayloadKind = UiPayloadKind.Page };

    private static UiPointCatalog Points() => new([PagePoint]);

    private static PageContributionProbe Page(string id, string route, params string[] requires) => new()
    { Point = PagePoint.Id, Id = id, Route = route, Title = id, ViewKey = id, Requires = requires };

    private static UiContributionEntry E(string owner, PageContributionProbe p) => new(owner, p);

    // ── D11：host.* 命名空间不可被模块占用 ──

    [Fact]
    public void ModuleCannotTakeHostNamespace()
    {
        // 若允许，模块 "webui" 会静默顶掉宿主 "host.webui" 的贡献
        var registry = new UiContributionRegistry(Points());
        registry.AddRange("host.webui", [Page("p.home", "/")]);

        registry.AddRange("webui", [Page("p.x", "/x", "webui")]);

        registry.All.Should().ContainSingle("宿主贡献不应被模块覆盖");
        registry.Diagnostics.Should().Contain(d => d.Kind == UiDiagnosticKind.CrossOwnerDuplicate);
    }

    [Fact]
    public void HostAndModuleCanCoexistUnderDifferentNamespaces()
    {
        var registry = new UiContributionRegistry(Points());

        registry.AddRange("host.webui", [Page("p.home", "/")]);
        registry.AddRange("memory", [Page("p.memory", "/memory", "memory")]);

        registry.All.Should().HaveCount(2);
        registry.Diagnostics.Should().BeEmpty();
    }

    // ── D10：迁移后路由集合完整性 ──

    [Fact]
    public void MigratedRouteSet_ShouldCoverAllLegacyRoutes()
    {
        // 迁移前 15 条模块路由（ModulePageRouteBinder.cs:23-56）+ 13 条壳层路由。
        // 决策 D10：模块自己声明全部 15 条（含 5 条曾被 enrich 补全的详情路由）。
        var moduleRoutes = new[]
        {
            ("memory", "/memory"), ("memory", "/memory/graph"), ("memory", "/memory/detail/{*FilePath}"),
            ("scheduler", "/cron-jobs"), ("scheduler", "/cron-jobs/{JobId}"), ("scheduler", "/heartbeat"),
            ("mcp", "/mcp"), ("acp", "/acp"),
            ("gateway", "/gateway"), ("gateway", "/gateway-clients"),
            ("skills", "/skills"), ("skills", "/skills/create"), ("skills", "/skills/{SkillName}"),
            ("basic", "/tools"), ("reflection", "/reflection")
        };

        var shellRoutes = new[]
        {
            ("host.webui", "/"), ("host.webui", "/home"), ("host.webui", "/workspaces"),
            ("host.webui", "/session"), ("host.webui", "/session/{SessionId}"),
            ("host.webui", "/conference"), ("host.webui", "/conference/{SessionId}"),
            ("host.webui", "/sessions"), ("host.webui", "/agents"), ("host.webui", "/agent-config"),
            ("host.webui", "/models"), ("host.webui", "/security"), ("host.webui", "/settings")
        };

        var registry = new UiContributionRegistry(Points());
        registry.AddRange("host.webui",
            shellRoutes.Select((r, i) => Page($"shell.{i}", r.Item2)).ToArray());

        foreach (var group in moduleRoutes.GroupBy(r => r.Item1))
        {
            registry.AddRange(group.Key,
                group.Select((r, i) => Page($"{group.Key}.{i}", r.Item2, group.Key)).ToArray());
        }

        registry.Pages.Select(p => p.Route).Should().BeEquivalentTo(
            moduleRoutes.Concat(shellRoutes).Select(r => r.Item2),
            "迁移后必须完整覆盖 15 + 13 条路由，缺任一条都会让直链掉 Fallback");

        registry.Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void TheFiveEnrichOnlyRoutes_MustBeDeclaredByModules()
    {
        // 这 5 条在旧实现里只由 ModulePageRouteBinder 的 enrich 产生（模块自己没声明）
        string[] enrichOnly =
        [
            "/memory/graph",
            "/memory/detail/{*FilePath}",
            "/cron-jobs/{JobId}",
            "/skills/create",
            "/skills/{SkillName}"
        ];

        var registry = new UiContributionRegistry(Points());

        registry.AddRange("host.webui", [Page("shell.home", "/")]);

        registry.AddRange("memory", [
            Page("memory.list", "/memory", "memory"),
            Page("memory.graph", "/memory/graph", "memory"),
            Page("memory.detail", "/memory/detail/{*FilePath}", "memory")
        ]);

        registry.AddRange("scheduler", [
            Page("cron.list", "/cron-jobs", "scheduler"),
            Page("cron.detail", "/cron-jobs/{JobId}", "scheduler")
        ]);

        registry.AddRange("skills", [
            Page("skills.list", "/skills", "skills"),
            Page("skills.create", "/skills/create", "skills"),
            Page("skills.detail", "/skills/{SkillName}", "skills")
        ]);

        var declared = registry.Pages.Select(p => p.Route).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var route in enrichOnly)
            declared.Should().Contain(route, $"'{route}' 必须由模块显式声明，否则迁移后直链 404");
    }

    // ── D12：选中键（GroupOrder 缺省语义见 SidebarProjectorTests）──

    [Fact]
    public void SelectedKeys_ForSessionPaths_ShouldBeChat()
    {
        SidebarProjector.SelectedKeys("session/abc", []).Should().Equal(SidebarProjector.ChatKey);
        SidebarProjector.SelectedKeys("", []).Should().Equal(SidebarProjector.ChatKey);
    }
}
