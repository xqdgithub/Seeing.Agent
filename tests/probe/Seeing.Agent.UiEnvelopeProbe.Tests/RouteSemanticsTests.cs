using FluentAssertions;
using Seeing.Agent.UiEnvelopeProbe;
using Xunit;

namespace Seeing.Agent.UiEnvelopeProbe.Tests;

/// <summary>
/// 验证：路由契约能否表达现状 15 条路由 + catch-all 语义。
/// 现状来源：ModuleRouteResolver.TryMatchTemplate / ModuleRouteResolverCatchAllTests。
/// </summary>
public class RouteSemanticsTests
{
    private static PageContributionProbe Page(string id, string route) => new()
    { Point = "shell.page", Id = id, Route = route, Title = id, ViewKey = id };

    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("session", "/session")]
    [InlineData("/session/", "/session")]
    [InlineData("/session?x=1", "/session")]
    [InlineData("/session#frag", "/session")]
    public void NormalizePath_MatchesLegacy(string? input, string expected)
        => UiRouteTemplate.NormalizePath(input).Should().Be(expected);

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
        var card = new SettingsCardContributionProbe
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
}
