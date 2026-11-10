using FluentAssertions;
using Seeing.Agent.UiEnvelopeProbe;
using Xunit;

namespace Seeing.Agent.UiEnvelopeProbe.Tests;

/// <summary>
/// 验证：注册表契约的行为是否可完整实现（幂等、诊断、no-op、异常隔离）。
/// </summary>
public class RegistrySemanticsTests
{
    private static readonly UiPoint PagePoint = new()
    { Id = "shell.page", DisplayName = "页面", PayloadKind = UiPayloadKind.Page };

    private static readonly UiPoint CardPoint = new()
    { Id = "shell.settings.card", DisplayName = "设置卡片", PayloadKind = UiPayloadKind.SettingsCard };

    private static readonly UiPoint SlotPoint = new()
    { Id = "session.prompt", DisplayName = "插槽", PayloadKind = UiPayloadKind.Slot, Scope = UiVisibilityScope.Session };

    private static UiPointCatalog Points() => new([PagePoint, CardPoint, SlotPoint]);

    private static PageContributionProbe Page(string id, string route, params string[] requires) => new()
    { Point = PagePoint.Id, Id = id, Route = route, Title = id, ViewKey = id, Requires = requires.Length > 0 ? requires : ["m"] };

    // ── 无界面宿主：整段 no-op ──

    [Fact]
    public void NoPointCatalog_ShouldBeNoOp()
    {
        var registry = new UiContributionRegistry(points: null);

        registry.AddRange("mcp", [Page("p", "/mcp", "mcp")]);

        registry.IsEnabled.Should().BeFalse();
        registry.All.Should().BeEmpty();
        registry.Diagnostics.Should().BeEmpty();   // 关键：无界面宿主不产生诊断噪声
    }

    // ── 收集与查询 ──

    [Fact]
    public void AddRange_ShouldCollectWithOwner()
    {
        var registry = new UiContributionRegistry(Points());
        registry.AddRange("mcp", [Page("page.mcp", "/mcp", "mcp")]);

        registry.All.Should().ContainSingle();
        registry.All[0].OwnerId.Should().Be("mcp");
        registry.Pages.Should().ContainSingle();
    }

    [Fact]
    public void Get_ShouldNotApplyVisibility()
    {
        // 注册表只给事实；未启用模块的声明仍在（供 ModuleRouter 产出 NotEnabled）
        var registry = new UiContributionRegistry(Points());
        registry.AddRange("skills", [Page("p", "/skills", "skills")]);

        registry.Get<PageContributionProbe>(PagePoint.Id).Should().ContainSingle();
        registry.Pages.Should().ContainSingle();
    }

    [Fact]
    public void Get_UnsupportedPoint_ShouldReturnEmpty()
    {
        var registry = new UiContributionRegistry(Points());
        registry.Get<PageContributionProbe>("nope").Should().BeEmpty();
    }

    [Fact]
    public void All_OrderedByOwnerThenPointThenOrderThenId()
    {
        var registry = new UiContributionRegistry(Points());
        registry.AddRange("zeta", [Page("p2", "/z", "zeta")]);
        registry.AddRange("alpha", [Page("p1", "/a", "alpha")]);

        registry.All.Select(e => e.OwnerId).Should().Equal("alpha", "zeta");
    }

    // ── 幂等（Deactivate→Activate 循环）──

    [Fact]
    public void AddRange_SameOwnerTwice_ShouldReplace()
    {
        var registry = new UiContributionRegistry(Points());

        registry.AddRange("mcp", [Page("p", "/mcp", "mcp")]);
        registry.AddRange("mcp", [Page("p", "/mcp", "mcp")]);

        registry.All.Should().ContainSingle();
    }

    [Fact]
    public void AddRange_PointChanged_ShouldNotLeaveStale()
    {
        var registry = new UiContributionRegistry(Points());
        registry.AddRange("m", [Page("x", "/x", "m")]);

        registry.AddRange("m", [new SlotContributionProbe
        { Point = SlotPoint.Id, Id = "x", ViewKey = "s", Requires = ["m"] }]);

        registry.All.Should().ContainSingle();
        registry.All[0].Contribution.Point.Should().Be(SlotPoint.Id);
    }

    [Fact]
    public void RemoveByOwner_ShouldBeIdempotent()
    {
        var registry = new UiContributionRegistry(Points());
        registry.AddRange("m", [Page("p", "/p", "m")]);

        registry.RemoveByOwner("m");
        registry.RemoveByOwner("m");

        registry.All.Should().BeEmpty();
    }

    // ── 校验 ──

    [Fact]
    public void UnsupportedPoint_ShouldBeRejected()
    {
        var registry = new UiContributionRegistry(Points());
        registry.AddRange("m", [new PageContributionProbe
        { Point = "nope", Id = "p", Route = "/p", Title = "P", ViewKey = "p" }]);

        registry.All.Should().BeEmpty();
        registry.Diagnostics.Should().ContainSingle().Which.Kind.Should().Be(UiDiagnosticKind.UnsupportedPoint);
    }

    [Fact]
    public void PayloadMismatch_ShouldBeRejected()
    {
        var registry = new UiContributionRegistry(Points());
        registry.AddRange("m", [new PageContributionProbe
        { Point = SlotPoint.Id, Id = "p", Route = "/p", Title = "P", ViewKey = "p" }]);

        registry.Diagnostics.Should().ContainSingle().Which.Kind.Should().Be(UiDiagnosticKind.PayloadMismatch);
    }

    [Fact]
    public void DuplicateIdInSameBatch_ShouldRejectSecond()
    {
        var registry = new UiContributionRegistry(Points());
        registry.AddRange("m", [Page("dup", "/a", "m"), Page("dup", "/b", "m")]);

        registry.All.Should().ContainSingle();
        registry.Diagnostics.Should().ContainSingle().Which.Kind.Should().Be(UiDiagnosticKind.DuplicateId);
    }

    [Fact]
    public void DuplicateRouteInSameBatch_ShouldRejectSecond()
    {
        var registry = new UiContributionRegistry(Points());
        registry.AddRange("m", [Page("a", "/same", "m"), Page("b", "/same", "m")]);

        registry.All.Should().ContainSingle();
        registry.Diagnostics.Should().ContainSingle().Which.Kind.Should().Be(UiDiagnosticKind.DuplicateRoute);
    }

    [Fact]
    public void MaxItems_ShouldRejectExtra()
    {
        var single = PagePoint with { Id = "single", MaxItems = 1 };
        var registry = new UiContributionRegistry(new UiPointCatalog([single]));

        registry.AddRange("m", [
            new PageContributionProbe { Point = single.Id, Id = "a", Route = "/a", Title = "A", ViewKey = "a", Requires = ["m"] },
            new PageContributionProbe { Point = single.Id, Id = "b", Route = "/b", Title = "B", ViewKey = "b", Requires = ["m"] }
        ]);

        registry.All.Should().ContainSingle();
        registry.Diagnostics.Should().ContainSingle().Which.Kind.Should().Be(UiDiagnosticKind.ExceededMaxItems);
    }

    [Fact]
    public void NullElement_ShouldBeRejectedNotThrow()
    {
        var registry = new UiContributionRegistry(Points());

        var act = () => registry.AddRange("m", [null!, Page("ok", "/ok", "m")]);

        act.Should().NotThrow();
        registry.All.Should().ContainSingle();
        registry.Diagnostics.Should().ContainSingle().Which.Kind.Should().Be(UiDiagnosticKind.InvalidDeclaration);
    }

    [Fact]
    public void Diagnostics_ShouldNotAccumulateOnRepeatedAdd()
    {
        var registry = new UiContributionRegistry(Points());
        var bad = new PageContributionProbe
        { Point = "nope", Id = "p", Route = "/p", Title = "P", ViewKey = "p" };

        registry.AddRange("m", [bad]);
        registry.AddRange("m", [bad]);

        registry.Diagnostics.Should().ContainSingle();
    }

    [Fact]
    public void ValidatorRejection_ShouldBeRecorded()
    {
        var registry = new UiContributionRegistry(Points(), [new RejectingValidator()]);

        registry.AddRange("m", [Page("p", "/p", "m")]);

        registry.All.Should().BeEmpty();
        registry.Diagnostics.Should().ContainSingle().Which.Kind.Should().Be(UiDiagnosticKind.ValidatorRejected);
    }

    [Fact]
    public void ThrowingValidator_ShouldRejectNotPropagate()
    {
        var registry = new UiContributionRegistry(Points(), [new ThrowingValidator()]);

        var act = () => registry.AddRange("m", [Page("p", "/p", "m")]);

        act.Should().NotThrow();
        registry.Diagnostics.Should().ContainSingle().Which.Kind.Should().Be(UiDiagnosticKind.ValidatorRejected);
    }

    // ── 异常隔离 ──

    [Fact]
    public void Changed_SubscriberThrows_ShouldNotPropagate()
    {
        var registry = new UiContributionRegistry(Points());
        registry.Changed += (_, _) => throw new InvalidOperationException("boom");

        var act = () => registry.AddRange("m", [Page("p", "/p", "m")]);

        act.Should().NotThrow();
        registry.All.Should().ContainSingle();
    }

    [Fact]
    public void Changed_ShouldFireOnAddAndRemove()
    {
        var registry = new UiContributionRegistry(Points());
        var fired = 0;
        registry.Changed += (_, _) => fired++;

        registry.AddRange("m", [Page("p", "/p", "m")]);
        registry.RemoveByOwner("m");

        fired.Should().Be(2);
    }

    private sealed class RejectingValidator : IUiContributionValidator
    {
        public string? Validate(UiContribution contribution, UiPoint point) => "视图未装配";
    }

    private sealed class ThrowingValidator : IUiContributionValidator
    {
        public string? Validate(UiContribution contribution, UiPoint point)
            => throw new InvalidOperationException("validator boom");
    }
}

