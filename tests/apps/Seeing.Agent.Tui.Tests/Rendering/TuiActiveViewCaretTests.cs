using FluentAssertions;
using Seeing.Agent.Tui.Input;
using Seeing.Agent.Tui.Rendering;
using Seeing.Agent.Tui.Services;

namespace Seeing.Agent.Tui.Tests.Rendering;

/// <summary>
/// 活动区光标几何回归：终端物理光标必须落在输入区插入点上，否则输入法组合串会画到
/// 活动区末行（状态栏）而不是用户正在输入的位置。
/// <para>
/// 坐标定义：<c>RowsBelow</c> = 插入点之下还有几行（输入行剩余行 + 状态栏行），
/// <c>Column</c> = 1 基终端列（<c>&gt; </c> 前缀 2 格 + 插入点前的显示宽度 + 1）。
/// </para>
/// </summary>
public sealed class TuiActiveViewCaretTests
{
    private static readonly TuiRenderer Renderer = new(new TuiRenderOptions());

    private static TuiCaret? BuildCaret(
        string text,
        int cursor,
        string? workspace = null,
        TuiBudget? budget = null,
        int maxLines = 0)
    {
        var input = new TuiInputEditorState();
        input.SetTextAndCursor(text, cursor);

        var state = new TuiViewState { SessionId = "ses_1", AgentId = "build", WorkspaceRoot = workspace, Budget = budget };
        return Renderer.BuildActiveViewWithCaret(state, input, 100, maxLines: maxLines).Caret;
    }

    [Fact]
    public void Caret_WhenInputEmpty_ShouldSitRightAfterPromptPrefix()
    {
        // "> " 占 2 格，插入点在第 3 列；状态栏无第 2 行时插入点下只有 1 行。
        BuildCaret(string.Empty, 0).Should().Be(new TuiCaret(1, 3));
    }

    [Fact]
    public void Caret_WhenCursorAtEnd_ShouldSitAfterWholeText()
        => BuildCaret("abcdef", 6).Should().Be(new TuiCaret(1, 9));

    [Fact]
    public void Caret_WhenCursorInMiddle_ShouldFollowCursorNotTextEnd()
        => BuildCaret("abcdef", 3).Should().Be(new TuiCaret(1, 6));

    [Fact]
    public void Caret_WhenCjkBeforeCursor_ShouldCountTwoCellsPerCharacter()
        => BuildCaret("中文", 1).Should().Be(new TuiCaret(1, 5));

    [Fact]
    public void Caret_WhenMixedWidthBeforeCursor_ShouldSumDisplayWidth()
        => BuildCaret("a中b", 2).Should().Be(new TuiCaret(1, 6));

    [Fact]
    public void Caret_WhenCaretOnLastInputLine_ShouldNotCountInputRowsBelow()
        => BuildCaret("abc\ndef", 7).Should().Be(new TuiCaret(1, 6));

    [Fact]
    public void Caret_WhenCaretOnFirstInputLine_ShouldCountRemainingInputLines()
        => BuildCaret("abc\ndef", 2).Should().Be(new TuiCaret(2, 5));

    [Fact]
    public void Caret_WhenStatusBarHasSecondRow_ShouldCountItAsRowBelow()
        => BuildCaret("abc", 3, workspace: @"D:\work").Should().Be(new TuiCaret(2, 6));

    [Fact]
    public void Caret_WhenStatusBarHasSecondRowFromBudgetOnly_ShouldCountItAsRowBelow()
        => BuildCaret("abc", 3, budget: new TuiBudget(1200, 200000)).Should().Be(new TuiCaret(2, 6));

    [Fact]
    public void Caret_WhenInputLineIsClippedByMaxLines_ShouldNotPlaceCaret()
        => BuildCaret("abc", 3, maxLines: 1).Should().BeNull();

    [Fact]
    public void BuildActiveView_WithoutCaretOverload_ShouldStillRenderInputLine()
    {
        var state = new TuiViewState { SessionId = "ses_1", AgentId = "build" };
        var input = new TuiInputEditorState();
        input.SetTextAndCursor("abc", 1);

        var view = Renderer.BuildActiveView(state, input, 100);
        new Fakes.FakeTerminalSurface().RenderToString(view).Should().Contain($"a{TuiGlyphs.Cursor}bc");
    }
}
