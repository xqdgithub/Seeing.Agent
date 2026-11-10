using FluentAssertions;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Core.Scenarios;
using Seeing.Agent.UiEnvelopeProbe;

namespace Seeing.Agent.UiEnvelopeProbe.Tests;

/// <summary>
/// 等价性审查发现的差异，逐条钉住。
/// 每项都注明对照的旧实现位置。
/// </summary>
public class EquivalenceGapTests
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

    private sealed class FakeScenarioCatalog : IScenarioCatalog
    {
        private readonly Dictionary<string, ScenarioDefinition> _byName = new(StringComparer.OrdinalIgnoreCase);

        public static FakeScenarioCatalog With(string name, string[] modules)
        {
            var catalog = new FakeScenarioCatalog();
            catalog._byName[name] = new ScenarioDefinition(
                Name: name,
                Modules: modules,
                DefaultAgent: "build",
                Seams: new Dictionary<string, string>(),
                ToolsDisabled: []);
            return catalog;
        }

        public ScenarioDefinition? Get(string name)
            => _byName.GetValueOrDefault(name);

        public IReadOnlyList<ScenarioDefinition> ListAll() => _byName.Values.ToArray();

        public bool IsBuiltInName(string name) => false;

        public bool HasConfiguredOverride(string name) => _byName.ContainsKey(name);
    }

    private static readonly UiPoint PagePoint = new()
    { Id = "shell.page", DisplayName = "页面", PayloadKind = UiPayloadKind.Page };

    private static PageContributionProbe Page(string id, string route, params string[] requires) => new()
    { Point = PagePoint.Id, Id = id, Route = route, Title = id, ViewKey = id, Requires = requires };

    // ── G1：catalog 缺失时壳层仍可见（旧 UiContributionVisibility.cs:85-87）──

    [Fact]
    public void NullCatalog_ShellPage_ShouldStayVisible()
    {
        // 旧实现：requires.Count == 0 先返回 true，再看 catalog。
        // 若壳层也隐藏，用户会失去 /settings 而无法自救。
        UiVisibility.IsVisible(Page("p.home", "/"), PagePoint, UiVisibilityContext.Process(null))
            .Should().BeTrue();
    }

    [Fact]
    public void NullCatalog_ModulePage_ShouldBeHidden()
    {
        UiVisibility.IsVisible(Page("p.skills", "/skills", "skills"), PagePoint,
            UiVisibilityContext.Process(null)).Should().BeFalse();
    }

    // ── G2：UiScenarioGate 五个分支（旧 SessionShellVisibility.cs:115-127）──

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ScenarioGate_Blank_ShouldNotNarrow(string? scenario)
        => UiScenarioGate.Resolve(scenario, catalog: null).Should().BeNull();

    [Fact]
    public void ScenarioGate_Unknown_ShouldNotNarrow()
        => UiScenarioGate.Resolve("no-such-scenario", catalog: null).Should().BeNull();

    [Fact]
    public void ScenarioGate_Wildcard_ShouldNotNarrow()
    {
        // 内置 full 的 Modules = ["*"]
        UiScenarioGate.Resolve("full", catalog: null).Should().BeNull();
    }

    [Fact]
    public void ScenarioGate_ExplicitModules_ShouldBeCaseInsensitive()
    {
        var gate = UiScenarioGate.Resolve("custom", FakeScenarioCatalog.With("custom", ["memory", "Skills"]));

        gate.Should().NotBeNull();
        gate!.Contains("skills").Should().BeTrue("门集必须 OrdinalIgnoreCase");
        gate.Contains("MEMORY").Should().BeTrue();
    }

    [Fact]
    public void ScenarioGate_EmptyModules_ShouldReturnEmptySetNot_Null()
    {
        // 空集 = 全部隐藏；null = 不收敛。两者语义相反。
        var gate = UiScenarioGate.Resolve("empty", FakeScenarioCatalog.With("empty", []));

        gate.Should().NotBeNull();
        gate!.Should().BeEmpty();
    }

    [Fact]
    public void ResolveEffective_SessionWins_ElseProcess()
    {
        UiScenarioGate.ResolveEffective("work", "full").Should().Be("work");
        UiScenarioGate.ResolveEffective(null, "full").Should().Be("full");
        UiScenarioGate.ResolveEffective("  ", " full ").Should().Be("full");
        UiScenarioGate.ResolveEffective(null, null).Should().BeNull();
    }

    // ── G3：Resolve 必须能排除不可见页（旧实现停用模块路由不在表内）──

    [Fact]
    public void Resolve_WithVisibilityPredicate_DisabledPageMustNotShadowEnabled()
    {
        var ctx = UiVisibilityContext.Process(new FakeCatalog());   // memory 停用

        // 停用的那页分数更高（catch-all 更长）
        var disabled = new UiContributionEntry("memory",
            Page("memory.detail", "/memory/detail/{*FilePath}", "memory"));
        var enabled = new UiContributionEntry("host.webui",
            Page("host.detail", "/memory/detail/{*File}"));

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
            Page("memory.detail", "/memory/detail/{*FilePath}", "memory"));
        var enabled = new UiContributionEntry("host.webui",
            Page("host.detail", "/memory/detail/{*File}"));

        UiRouteTemplate.Resolve("/memory/detail/a.md", [disabled, enabled])!
            .Page.Id.Should().Be("memory.detail");
    }

    // ── G4：NormalizePath 边界（旧 ModuleRouteResolver.cs:82-101）──

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

    // ── G5：路由 tiebreak 确定性（旧实现是先到者赢，新实现与顺序无关）──

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
}
