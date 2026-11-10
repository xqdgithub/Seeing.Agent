using FluentAssertions;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Ui;
using Seeing.Agent.Core.Ui;
using Xunit;

namespace Seeing.Agent.Tests.Ui;

/// <summary>
/// 路由模板语义：规范化、参数、catch-all、打分与确定性 tiebreak。
/// <para>
/// 现状来源：<c>ModuleRouteResolver.TryMatchTemplate</c> / <c>ModuleRouteResolverCatchAllTests</c>。
/// </para>
/// </summary>
public class UiRouteTemplateTests
{
    private sealed class FakeCatalog : IModuleCatalog
    {
        private readonly HashSet<string> _enabled;

        public FakeCatalog(params string[] enabled)
            => _enabled = new HashSet<string>(enabled, StringComparer.OrdinalIgnoreCase);

        public IReadOnlyCollection<ModuleDescriptor> Available => [];
        public IReadOnlyCollection<string> Enabled => _enabled;
        public IReadOnlyDictionary<string, string?> Unhealthy => new Dictionary<string, string?>();
        public bool IsEnabled(string id) => _enabled.Contains(id);
        public bool IsAvailable(string id) => true;
    }

    private static readonly UiPoint PagePoint = new()
    { Id = "shell.page", DisplayName = "页面", PayloadKind = UiPayloadKind.Page };

    private static PageContribution Page(string id, string route) => new()
    { Point = "shell.page", Id = id, Route = route, Title = id, ViewKey = id };

    // ── NormalizePath ──

    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("session", "/session")]
    [InlineData("/session/", "/session")]
    [InlineData("/session?x=1", "/session")]
    [InlineData("/session#frag", "/session")]
    public void NormalizePath_MatchesLegacy(string? input, string expected)
        => UiRouteTemplate.NormalizePath(input).Should().Be(expected);

    [Theory]
    [InlineData("//", "/")]
    [InlineData("///", "/")]
    [InlineData("/a//b", "/a//b")]
    [InlineData("?x=1", "/")]
    [InlineData("#frag", "/")]
    [InlineData("/a/?b#c", "/a")]
    [InlineData("a/b/", "/a/b")]
    public void NormalizePath_EdgeCases(string input, string expected)
        => UiRouteTemplate.NormalizePath(input).Should().Be(expected);

    // ── 参数与 catch-all ──

    [Fact]
    public void ParameterKey_ShouldEqualTemplatePlaceholderName()
    {
        // 这条是跨程序集的纯字符串契约：模板 {name} → 页面 [Parameter] name
        UiRouteTemplate.TryMatch("/cron-jobs/{JobId}", "/cron-jobs/abc", out var p, out _).Should().BeTrue();
        p.Should().ContainKey("JobId");
        p["JobId"].Should().Be("abc");
    }

    [Fact]
    public void CatchAll_ShouldJoinRemainingSegments()
    {
        UiRouteTemplate.TryMatch("/memory/detail/{*FilePath}", "/memory/detail/daily/2026-10-18/a.md",
            out var p, out _).Should().BeTrue();
        p["FilePath"].Should().Be("daily/2026-10-18/a.md");
    }

    [Fact]
    public void CatchAll_NoTail_ShouldCaptureEmpty()
    {
        UiRouteTemplate.TryMatch("/memory/detail/{*FilePath}", "/memory/detail", out var p, out _).Should().BeTrue();
        p["FilePath"].Should().Be(string.Empty);
    }

    [Fact]
    public void CatchAll_ShouldNotUrlDecode()
    {
        UiRouteTemplate.TryMatch("/memory/detail/{*FilePath}", "/memory/detail/a%2Fb", out var p, out _)
            .Should().BeTrue();
        p["FilePath"].Should().Be("a%2Fb");
    }

    [Fact]
    public void CatchAll_ShouldNotSwallowLiteralSibling()
    {
        // /memory/graph 不能被 /memory/detail/{*FilePath} 吃掉（既有测试钉死）
        UiRouteTemplate.TryMatch("/memory/detail/{*FilePath}", "/memory/graph", out _, out _).Should().BeFalse();
        UiRouteTemplate.TryMatch("/memory/graph", "/memory/graph", out _, out _).Should().BeTrue();
    }

    [Fact]
    public void Parameter_ShouldNotMatchExtraSegments()
        => UiRouteTemplate.TryMatch("/cron-jobs/{JobId}", "/cron-jobs/abc/edit", out _, out _).Should().BeFalse();

    [Fact]
    public void MoreLiterals_ShouldScoreHigher()
    {
        UiRouteTemplate.TryMatch("/cron-jobs", "/cron-jobs", out _, out var literal).Should().BeTrue();
        UiRouteTemplate.TryMatch("/{a}", "/cron-jobs", out _, out var param).Should().BeTrue();
        literal.Should().BeGreaterThan(param);
    }

    // ── Resolve ──

    [Fact]
    public void Resolve_LiteralBeatsParameter()
    {
        var entries = new[]
        {
            new UiContributionEntry("scheduler", Page("cron", "/cron-jobs")),
            new UiContributionEntry("scheduler", Page("cron.detail", "/cron-jobs/{JobId}"))
        };

        UiRouteTemplate.Resolve("/cron-jobs/abc", entries)!.Page.Id.Should().Be("cron.detail");
        UiRouteTemplate.Resolve("/cron-jobs", entries)!.Page.Id.Should().Be("cron");
    }

    [Fact]
    public void Resolve_LiteralBeatsCatchAll()
    {
        var entries = new[]
        {
            new UiContributionEntry("memory", Page("detail", "/memory/detail/{*FilePath}")),
            new UiContributionEntry("memory", Page("graph", "/memory/graph"))
        };

        UiRouteTemplate.Resolve("/memory/graph", entries)!.Page.Id.Should().Be("graph");
        UiRouteTemplate.Resolve("/memory/detail/a.md", entries)!.Page.Id.Should().Be("detail");
    }

    [Fact]
    public void Resolve_Deterministic_RegardlessOfOrder()
    {
        var a = new UiContributionEntry("m1", Page("a", "/x/{p1}"));
        var b = new UiContributionEntry("m2", Page("b", "/x/{p2}"));

        var r1 = UiRouteTemplate.Resolve("/x/1", [a, b])!;
        var r2 = UiRouteTemplate.Resolve("/x/1", [b, a])!;

        r1.Page.Id.Should().Be(r2.Page.Id);
    }

    [Fact]
    public void Resolve_NoMatch_ShouldBeNull()
        => UiRouteTemplate.Resolve("/nope",
            [new UiContributionEntry("m", Page("p", "/memory"))]).Should().BeNull();

    [Fact]
    public void Resolve_ShouldIgnoreNonPageContributions()
    {
        var card = new SettingsCardContribution
        { Point = "shell.settings.card", Id = "c", Title = "C", ViewKey = "c" };

        UiRouteTemplate.Resolve("/anything", [new UiContributionEntry("host.webui", card)])
            .Should().BeNull();
    }

    [Fact]
    public void Resolve_AllFifteenRealRoutes_ShouldHit()
    {
        // 用真实的 15 条路由 + 对应的典型访问路径验证覆盖率
        var routes = new (string Id, string Route, string Probe)[]
        {
            ("memory", "/memory", "/memory"),
            ("memory.graph", "/memory/graph", "/memory/graph"),
            ("memory.detail", "/memory/detail/{*FilePath}", "/memory/detail/a/b.md"),
            ("cron", "/cron-jobs", "/cron-jobs"),
            ("cron.detail", "/cron-jobs/{JobId}", "/cron-jobs/job_1"),
            ("heartbeat", "/heartbeat", "/heartbeat"),
            ("mcp", "/mcp", "/mcp"),
            ("acp", "/acp", "/acp"),
            ("gateway", "/gateway", "/gateway"),
            ("gateway.clients", "/gateway-clients", "/gateway-clients"),
            ("skills", "/skills", "/skills"),
            ("skills.create", "/skills/create", "/skills/create"),
            ("skills.detail", "/skills/{SkillName}", "/skills/my-skill"),
            ("tools", "/tools", "/tools"),
            ("reflection", "/reflection", "/reflection")
        };

        var entries = routes.Select(r => new UiContributionEntry("m", Page(r.Id, r.Route))).ToArray();

        foreach (var (id, _, probe) in routes)
        {
            var match = UiRouteTemplate.Resolve(probe, entries);
            match.Should().NotBeNull($"路由 {probe} 应有匹配");
            match!.Page.Id.Should().Be(id, $"路径 {probe} 应命中 {id}，实际命中 {match.Page.Id}");
        }
    }

    // ── 等价性差距 G3：Resolve 必须能排除不可见页（旧实现停用模块路由不在表内）──

    [Fact]
    public void Resolve_WithVisibilityPredicate_DisabledPageMustNotShadowEnabled()
    {
        var ctx = UiVisibilityContext.Process(new FakeCatalog());   // memory 停用

        // 停用的那页分数更高（catch-all 更长）
        var disabled = new UiContributionEntry("memory",
            PageWith("memory.detail", "/memory/detail/{*FilePath}", "memory"));
        var enabled = new UiContributionEntry("host.webui",
            PageWith("host.detail", "/memory/detail/{*File}"));

        var match = UiRouteTemplate.Resolve(
            "/memory/detail/a.md",
            [disabled, enabled],
            e => UiVisibility.IsVisible(e.Contribution, PagePoint, ctx));

        match!.Page.Id.Should().Be("host.detail", "停用模块的页面不得抢赢启用模块的页面");
    }

    [Fact]
    public void Resolve_WithoutPredicate_ShowsShadowingRisk()
    {
        // 反例：不过滤时高分页胜出 —— 这就是为什么生产调用必须传谓词
        var disabled = new UiContributionEntry("memory",
            PageWith("memory.detail", "/memory/detail/{*FilePath}", "memory"));
        var enabled = new UiContributionEntry("host.webui",
            PageWith("host.detail", "/memory/detail/{*File}"));

        UiRouteTemplate.Resolve("/memory/detail/a.md", [disabled, enabled])!
            .Page.Id.Should().Be("memory.detail");
    }

    // ── 等价性差距 G5：路由 tiebreak 确定性（旧实现是先到者赢，新实现与顺序无关）──

    [Fact]
    public void SameScore_DifferentTemplates_ShouldBeOrderIndependent()
    {
        var upper = new UiContributionEntry("m", Page("a", "/skills/{SkillName}"));
        var lower = new UiContributionEntry("m", Page("b", "/skills/{Skillname}"));

        var r1 = UiRouteTemplate.Resolve("/skills/x", [lower, upper])!;
        var r2 = UiRouteTemplate.Resolve("/skills/x", [upper, lower])!;

        r1.Page.Id.Should().Be(r2.Page.Id);
    }

    [Fact]
    public void TrailingSlashVariant_ShouldBeDeterministic()
    {
        var plain = new UiContributionEntry("m", Page("plain", "/memory"));
        var slashed = new UiContributionEntry("m", Page("slashed", "/memory/"));

        UiRouteTemplate.Resolve("/memory", [slashed, plain])!.Page.Id
            .Should().Be(UiRouteTemplate.Resolve("/memory", [plain, slashed])!.Page.Id);
    }

    private static PageContribution PageWith(string id, string route, params string[] requires) => new()
    { Point = PagePoint.Id, Id = id, Route = route, Title = id, ViewKey = id, Requires = requires };
}
