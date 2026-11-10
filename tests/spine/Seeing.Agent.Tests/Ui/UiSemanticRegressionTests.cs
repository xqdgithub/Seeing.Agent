using FluentAssertions;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Ui;
using Seeing.Agent.Core.Ui;
using Xunit;

namespace Seeing.Agent.Tests.Ui;

/// <summary>
/// 回归测试：业务语义审查发现的三处回退。
/// 每项都对照「旧实现的真实行为」，不是凭设计意图断言。
/// </summary>
public class UiSemanticRegressionTests
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

    private static readonly UiPoint SessionPoint = new()
    {
        Id = "session.prompt", DisplayName = "插槽",
        PayloadKind = UiPayloadKind.Slot, Scope = UiVisibilityScope.Session
    };

    private static readonly UiPoint PagePoint = new()
    {
        Id = "shell.page", DisplayName = "页面", PayloadKind = UiPayloadKind.Page
    };

    private static SlotContribution Slot(params string[] requires) => new()
    { Point = SessionPoint.Id, Id = "s", ViewKey = "s", Requires = requires };

    // ── R1：多元素 Requires 必须是 AND（旧实现 SessionShellVisibility.cs:84-95）──

    [Fact]
    public void MultiRequires_GateMissingOne_ShouldBeHidden_AndSemantics()
    {
        // 旧实现：foreach { if (!contains) return false; }  → 两个都要在门集内
        var ctx = new UiVisibilityContext(
            new FakeCatalog("gateway", "comms"),
            new HashSet<string>(["gateway"], StringComparer.OrdinalIgnoreCase));   // 缺 comms

        var decision = UiVisibility.Evaluate(Slot("gateway", "comms"), SessionPoint, ctx);

        decision.Visible.Should().BeFalse("Requires 是 AND，缺 comms 应不可见");
        decision.Reason.Should().Be(UiVisibilityReason.ScenarioMismatch);
    }

    [Fact]
    public void MultiRequires_GateHasAll_ShouldBeVisible()
    {
        var ctx = new UiVisibilityContext(
            new FakeCatalog("gateway", "comms"),
            new HashSet<string>(["gateway", "comms"], StringComparer.OrdinalIgnoreCase));

        UiVisibility.IsVisible(Slot("gateway", "comms"), SessionPoint, ctx).Should().BeTrue();
    }

    // ── R2：空门集 = 全部隐藏（旧实现 Contains 全 false），null = 不收敛 ──

    [Fact]
    public void EmptyGate_ShouldHide_NotFailOpen()
    {
        // 触发路径：用户在工作模式编辑器里把模块全取消，或 seeing.json 写 "Modules": []
        var ctx = new UiVisibilityContext(
            new FakeCatalog("skills"),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));   // 空集

        var decision = UiVisibility.Evaluate(Slot("skills"), SessionPoint, ctx);

        decision.Visible.Should().BeFalse("空门集表示「一个模块都不启用」，不能解释为「不限制」");
        decision.Reason.Should().Be(UiVisibilityReason.ScenarioMismatch);
    }

    [Fact]
    public void NullGate_ShouldNotNarrow()
    {
        // null = 未指定工作模式（或声明了通配）→ 不收敛
        var ctx = new UiVisibilityContext(new FakeCatalog("skills"), ScenarioModuleGate: null);

        UiVisibility.IsVisible(Slot("skills"), SessionPoint, ctx).Should().BeTrue();
    }

    // ── R3：模块贡献空 Requires 应被拒（防「半残但看起来正常」的页面）──

    [Fact]
    public void ModuleOwner_EmptyRequires_ShouldBeRejected()
    {
        var registry = new UiContributionRegistry(new UiPointCatalog([PagePoint]));

        registry.AddRange("skills", [new PageContribution
        { Point = PagePoint.Id, Id = "p", Route = "/p", Title = "P", ViewKey = "p" }]);

        registry.All.Should().BeEmpty();
        registry.Diagnostics.Should().ContainSingle()
            .Which.Kind.Should().Be(UiDiagnosticKind.InvalidDeclaration);
    }

    [Fact]
    public void HostOwner_EmptyRequires_ShouldBeAccepted()
    {
        // 壳层必须始终可达
        var registry = new UiContributionRegistry(new UiPointCatalog([PagePoint]));

        registry.AddRange("host.webui", [new PageContribution
        { Point = PagePoint.Id, Id = "p", Route = "/p", Title = "P", ViewKey = "p" }]);

        registry.All.Should().ContainSingle();
        registry.Diagnostics.Should().BeEmpty();
    }

    // ── R4：跨 owner (point, id) 重复（否则 Settings.razor 的 @key 重复 → 渲染崩）──

    [Fact]
    public void CrossOwner_SamePointAndId_ShouldBeRejected()
    {
        var cardPoint = new UiPoint
        { Id = "shell.settings.card", DisplayName = "卡片", PayloadKind = UiPayloadKind.SettingsCard };
        var registry = new UiContributionRegistry(new UiPointCatalog([cardPoint]));

        // 壳层代 scheduler 声明
        registry.AddRange("host.webui", [new SettingsCardContribution
        { Point = cardPoint.Id, Id = "card.scheduler", Title = "调度器", ViewKey = "card.scheduler", Requires = ["scheduler"] }]);

        // scheduler 模块自己也声明同一张卡
        registry.AddRange("scheduler", [new SettingsCardContribution
        { Point = cardPoint.Id, Id = "card.scheduler", Title = "调度器", ViewKey = "card.scheduler", Requires = ["scheduler"] }]);

        registry.All.Should().ContainSingle("跨 owner 同 (point, id) 必须拒收，否则 Tab key 重复");
        registry.Diagnostics.Should().Contain(d => d.Kind == UiDiagnosticKind.CrossOwnerDuplicate);
    }

    [Fact]
    public void CrossOwner_DifferentId_ShouldCoexist()
    {
        var cardPoint = new UiPoint
        { Id = "shell.settings.card", DisplayName = "卡片", PayloadKind = UiPayloadKind.SettingsCard };
        var registry = new UiContributionRegistry(new UiPointCatalog([cardPoint]));

        registry.AddRange("host.webui", [new SettingsCardContribution
        { Point = cardPoint.Id, Id = "card.boot", Title = "启动", ViewKey = "card.boot" }]);
        registry.AddRange("scheduler", [new SettingsCardContribution
        { Point = cardPoint.Id, Id = "card.scheduler", Title = "调度器", ViewKey = "card.scheduler", Requires = ["scheduler"] }]);

        registry.All.Should().HaveCount(2);
    }

    [Fact]
    public void SameOwner_ReAddRange_ShouldNotTriggerCrossOwnerCheck()
    {
        // 幂等重登不能自己和自己冲突
        var registry = new UiContributionRegistry(new UiPointCatalog([PagePoint]));
        var page = new PageContribution
        { Point = PagePoint.Id, Id = "p", Route = "/p", Title = "P", ViewKey = "p", Requires = ["skills"] };

        registry.AddRange("skills", [page]);
        registry.AddRange("skills", [page]);

        registry.All.Should().ContainSingle();
        registry.Diagnostics.Should().BeEmpty();
    }
}
