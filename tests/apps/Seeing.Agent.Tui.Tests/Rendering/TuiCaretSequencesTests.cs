using FluentAssertions;
using Seeing.Agent.Tui.Rendering;

namespace Seeing.Agent.Tui.Tests.Rendering;

/// <summary>
/// 编辑光标定位序列回归：IME 组合串跟随物理光标位置，故「把光标移到插入点」与
/// 「放回活动区末行」两个序列必须精确可测（少移/多移都会让输入法框漂走）。
/// </summary>
public sealed class TuiCaretSequencesTests
{
    [Fact]
    public void Place_WhenCaretOnFrameLastLineAndFirstColumn_ShouldOnlyReturnCarriageReturn()
        => TuiCaretSequences.Place(new TuiCaret(0, 1)).Should().Be("\r");

    [Fact]
    public void Place_WhenCaretOnLastLine_ShouldMoveColumnOnly()
        => TuiCaretSequences.Place(new TuiCaret(0, 5)).Should().Be("\r\u001b[4C");

    [Fact]
    public void Place_WhenCaretOnFirstColumn_ShouldMoveRowOnly()
        => TuiCaretSequences.Place(new TuiCaret(2, 1)).Should().Be("\u001b[2A\r");

    [Fact]
    public void Place_WhenCaretMovedOnBothAxes_ShouldMoveRowThenColumn()
        => TuiCaretSequences.Place(new TuiCaret(3, 10)).Should().Be("\u001b[3A\r\u001b[9C");

    [Fact]
    public void Place_ShouldNeverEmitZeroStepMove()
    {
        // CSI 0 步在部分终端被当作 1 步：0 位移必须整段省略。
        TuiCaretSequences.Place(new TuiCaret(0, 1)).Should().NotContain("[0");
        TuiCaretSequences.Place(new TuiCaret(1, 1)).Should().NotContain("[0");
    }

    [Fact]
    public void Restore_WhenCaretWasNotMoved_ShouldReturnEmpty()
        => TuiCaretSequences.Restore(0).Should().BeEmpty();

    [Fact]
    public void Restore_WhenCaretWasMovedUp_ShouldMoveBackDown()
        => TuiCaretSequences.Restore(2).Should().Be("\u001b[2B");
}
