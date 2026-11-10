using FluentAssertions;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.UiEnvelopeProbe;

namespace Seeing.Agent.UiEnvelopeProbe.Tests;

/// <summary>
/// 验证：契约能否逐条表达现状的可见性语义。
/// 现状来源：UiContributionVisibility.cs / SessionShellVisibility.cs。
/// </summary>
public class VisibilitySemanticsTests
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

    private static readonly UiPoint ProcessPoint = new()
    {
        Id = "shell.page", DisplayName = "页面",
        PayloadKind = UiPayloadKind.Page, Scope = UiVisibilityScope.Process
    };

    private static readonly UiPoint SessionPoint = new()
    {
        Id = "session.prompt", DisplayName = "输入区上方",
        PayloadKind = UiPayloadKind.Slot, Scope = UiVisibilityScope.Session
    };

    private static PageContributionProbe Page(params string[] requires) => new()
    { Point = ProcessPoint.Id, Id = "p", Route = "/p", Title = "P", ViewKey = "p", Requires = requires };

    private static SlotContributionProbe Slot(params string[] requires) => new()
    { Point = SessionPoint.Id, Id = "s", ViewKey = "s", Requires = requires };

    // ── 现状：模块贡献的 Requires = 自身模块 id ──

    [Fact]
    public void Module_Enabled_ShouldBeVisible()
        => UiVisibility.IsVisible(Page("skills"), ProcessPoint,
            UiVisibilityContext.Process(new FakeCatalog("skills"))).Should().BeTrue();

    [Fact]
    public void Module_Disabled_ShouldBeHidden()
    {
        var decision = UiVisibility.Evaluate(Page("skills"), ProcessPoint,
            UiVisibilityContext.Process(new FakeCatalog("memory")));

        decision.Visible.Should().BeFalse();
        decision.Reason.Should().Be(UiVisibilityReason.ModuleDisabled);
        decision.Detail.Should().Be("skills");
    }

    // ── 现状：壳层页面 Requires = []，永远可见 ──

    [Fact]
    public void ShellPage_EmptyRequires_AlwaysVisible_EvenWithNoModulesEnabled()
        => UiVisibility.IsVisible(Page(), ProcessPoint,
            UiVisibilityContext.Process(new FakeCatalog())).Should().BeTrue();

    // ── 现状：catalog 为 null 时，有 Requires 的贡献 fail-closed；空 Requires（壳层）仍可见 ──

    [Fact]
    public void NullCatalog_ModuleContribution_FailClosed()
    {
        var decision = UiVisibility.Evaluate(Page("skills"), ProcessPoint, UiVisibilityContext.Process(null));
        decision.Visible.Should().BeFalse();
        decision.Reason.Should().Be(UiVisibilityReason.CatalogUnavailable);
    }

    [Fact]
    public void NullCatalog_ShellPage_StillVisible()
    {
        // 对照旧 UiContributionVisibility.cs:85-87：空 Requires 先返回 true，再看 catalog。
        // 壳层若也隐藏，用户会失去 /settings 而无法自救。
        UiVisibility.IsVisible(Page(), ProcessPoint, UiVisibilityContext.Process(null))
            .Should().BeTrue();
    }

    // ── 现状：ModuleCatalog.IsEnabled 对空白 id 抛；这里必须拦成不可见 ──

    [Fact]
    public void BlankRequire_ShouldBeInvalid_NotThrow()
    {
        var act = () => UiVisibility.Evaluate(Page("  "), ProcessPoint,
            UiVisibilityContext.Process(new FakeCatalog()));

        act.Should().NotThrow();
        act().Reason.Should().Be(UiVisibilityReason.InvalidRequire);
    }

    // ── 现状：侧栏/设置卡/页面不随会话工作模式收窄 ──

    [Fact]
    public void ProcessScope_IgnoreScenarioGate()
        => UiVisibility.IsVisible(Page("skills"), ProcessPoint,
            new UiVisibilityContext(
                new FakeCatalog("skills"),
                new HashSet<string>(["memory"], StringComparer.OrdinalIgnoreCase)))
            .Should().BeTrue();

    // ── 现状：插槽随会话工作模式收窄 ──

    [Fact]
    public void SessionScope_GateExcludesOwner_ShouldBeHidden()
    {
        var decision = UiVisibility.Evaluate(Slot("skills"), SessionPoint,
            new UiVisibilityContext(
                new FakeCatalog("skills", "memory"),
                new HashSet<string>(["memory"], StringComparer.OrdinalIgnoreCase)));

        decision.Visible.Should().BeFalse();
        decision.Reason.Should().Be(UiVisibilityReason.ScenarioMismatch);
    }

    [Fact]
    public void SessionScope_GateIncludesOwner_ShouldBeVisible()
        => UiVisibility.IsVisible(Slot("skills"), SessionPoint,
            new UiVisibilityContext(
                new FakeCatalog("skills"),
                new HashSet<string>(["skills"], StringComparer.OrdinalIgnoreCase)))
            .Should().BeTrue();

    [Fact]
    public void SessionScope_NullGate_ShouldNotNarrow()
        => UiVisibility.IsVisible(Slot("skills"), SessionPoint,
            new UiVisibilityContext(new FakeCatalog("skills"), ScenarioModuleGate: null))
            .Should().BeTrue();

    [Fact]
    public void SessionScope_ProcessLevelContribution_ShouldNotBeNarrowed()
        => UiVisibility.IsVisible(Slot(), SessionPoint,
            new UiVisibilityContext(
                new FakeCatalog(),
                new HashSet<string>(["memory"], StringComparer.OrdinalIgnoreCase)))
            .Should().BeTrue();
}
