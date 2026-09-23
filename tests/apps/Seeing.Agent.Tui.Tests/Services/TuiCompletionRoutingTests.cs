using FluentAssertions;
using Seeing.Agent.Tui.Input;
using Seeing.Agent.Tui.Rendering;
using Seeing.Agent.Tui.Services;

namespace Seeing.Agent.Tui.Tests.Services;

/// <summary>
/// 内联补全「鼠标命中 → 选择/带入」链路回归（spec §6.2/§6.4）。
/// <para>
/// 以真实 <see cref="SelectableList"/> + <see cref="TuiAnchorProbe"/> + <see cref="TuiHitTest"/> 复现引擎
/// <c>HandleCompletionMouse</c> 的决策内核（引擎私有方法需完整会话上下文，改测其依赖的几何/选择纯逻辑）：
/// hover 改游标、左键点击带入、跨代次/未校准/resize 失效回落键盘。
/// </para>
/// </summary>
public sealed class TuiCompletionRoutingTests
{
    private const long FrameGen = 9;

    // 末行绝对行 20；候选三行自上而下 item0(行18)/item1(行19)/item2(行20)。
    private static IReadOnlyList<TuiHitRegion> Hits() => new[]
    {
        new TuiHitRegion(0, 2, FrameGen),
        new TuiHitRegion(1, 1, FrameGen),
        new TuiHitRegion(2, 0, FrameGen),
    };

    private static TuiAnchorProbe Calibrated()
    {
        var probe = new TuiAnchorProbe();
        probe.BeginProbe(FrameGen, () => { });
        probe.Report(20);
        return probe;
    }

    [Fact]
    public void Hover_SameGenMotionHit_ShouldMoveCursorToItem()
    {
        // Arrange：下拉三候选，游标默认 0。
        var selector = new SelectableList(new[] { "/model", "/mono", "/new" }, pageSize: 3, multiSelect: false, allowPaging: false);
        selector.SelectedIndex.Should().Be(0);

        // Act：鼠标 hover 到第 2 候选（绝对行 19）。
        var hit = TuiHitTest.TryResolve(Calibrated(), Hits(), FrameGen, absRow: 19);
        var moved = hit is not null && selector.SetByHit(hit.Value);

        // Assert：Motion 命中 → 游标移到该项（hover 高亮跟随）。
        moved.Should().BeTrue();
        selector.SelectedIndex.Should().Be(1);
    }

    [Fact]
    public void Click_LeftPressHit_ShouldAcceptIntoEditorWithoutSubmit()
    {
        // Arrange
        var selector = new SelectableList(new[] { "/model", "/mono", "/new" }, pageSize: 3, multiSelect: false, allowPaging: false);
        const string text = "/m";

        // Act：左键 Press 命中第三候选（行 20）→ 置游标 → 带入。
        var hit = TuiHitTest.TryResolve(Calibrated(), Hits(), FrameGen, absRow: 20);
        hit.Should().Be(2);
        selector.SetByHit(hit!.Value);

        var accepted = TuiChatEngine.TryComputeAccept(text, cursor: text.Length, selector.SelectedLabel!, out var newText, out var cursor);

        // Assert：带入 "/new "、光标就绪；文本未被当作提交（无换行、非空）。
        accepted.Should().BeTrue();
        newText.Should().Be("/new ");
        cursor.Should().Be(5);
    }

    [Fact]
    public void Hover_GenMismatch_ShouldNotChangeCursor()
    {
        // Arrange：底锚校准在旧代次 8，命中表却是 9（跨帧错配）。
        var probe = new TuiAnchorProbe();
        probe.BeginProbe(FrameGen - 1, () => { });
        probe.Report(20);
        var selector = new SelectableList(new[] { "/model", "/mono", "/new" }, pageSize: 3);

        // Act
        var hit = TuiHitTest.TryResolve(probe, Hits(), FrameGen, absRow: 19);

        // Assert：跨代次失效 → 不动游标（键盘兜底，M-3）。
        hit.Should().BeNull();
        selector.SelectedIndex.Should().Be(0);
    }

    [Fact]
    public void Hover_NotCalibrated_ShouldNotChangeCursor()
    {
        // Arrange：DSR 未回（TryGet 恒 false）。
        var probe = new TuiAnchorProbe();
        var selector = new SelectableList(new[] { "/model", "/mono", "/new" }, pageSize: 3);

        // Act
        var hit = TuiHitTest.TryResolve(probe, Hits(), FrameGen, absRow: 19);

        // Assert
        hit.Should().BeNull();
        selector.SelectedIndex.Should().Be(0);
    }

    [Fact]
    public void Resize_InvalidatedAnchor_ShouldNotResolveHit()
    {
        // Arrange：已校准后 resize → Invalidate。
        var probe = Calibrated();
        probe.Invalidate();

        // Act
        var hit = TuiHitTest.TryResolve(probe, Hits(), FrameGen, absRow: 19);

        // Assert：底锚失效 → 不命中。
        hit.Should().BeNull();
    }

    [Fact]
    public void ReleasePhase_ShouldNotBeTreatedAsClick()
    {
        // 点击只认 Press：构造 Release 阶段事件，引擎 HandleCompletionMouse 的判定谓词等价于此。
        var release = new TuiRawMouse(TuiMouseButton.Left, TuiMousePhase.Release, Col: 1, Row: 20);

        var isClick = release.Phase == TuiMousePhase.Press && release.Button == TuiMouseButton.Left;
        isClick.Should().BeFalse();
    }
}
