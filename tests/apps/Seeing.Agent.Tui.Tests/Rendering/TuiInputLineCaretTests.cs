using FluentAssertions;
using Seeing.Agent.Tui.Input;
using Seeing.Agent.Tui.Rendering;
using Seeing.Agent.Tui.Services;
using Seeing.Agent.Tui.Tests.Fakes;

namespace Seeing.Agent.Tui.Tests.Rendering;

/// <summary>
/// 输入行光标位置与用户消息回显回归：光标按真实插入位渲染，用户消息必须写入滚动历史。
/// </summary>
public sealed class TuiInputLineCaretTests
{
    private static readonly TuiRenderer Renderer = new(new TuiRenderOptions());

    private static string RenderInput(string text, int cursor)
    {
        var input = new TuiInputEditorState();
        input.SetTextAndCursor(text, cursor);

        var state = new TuiViewState { SessionId = "ses_1", AgentId = "build" };
        var surface = new FakeTerminalSurface();
        return surface.RenderToString(Renderer.BuildActiveView(state, input, 100));
    }

    [Fact]
    public void BuildInputLine_WithCursorInMiddle_ShouldKeepTextOnBothSides()
    {
        var text = RenderInput("abcdef", 3);

        text.Should().Contain($"abc{TuiGlyphs.Cursor}def");
    }

    [Fact]
    public void BuildInputLine_WithCursorAtEnd_ShouldAppendCaretAfterText()
    {
        var text = RenderInput("abcdef", 6);

        text.Should().Contain($"abcdef{TuiGlyphs.Cursor}");
    }

    [Fact]
    public void BuildInputLine_WithEmptyText_ShouldRenderPromptAndCaret()
    {
        var text = RenderInput(string.Empty, 0);

        text.Should().Contain(TuiGlyphs.Cursor);
    }

    [Fact]
    public void BuildActiveView_WithCompletions_ShouldRenderCandidatesAboveInputLine()
    {
        var state = new TuiViewState { SessionId = "ses_1", AgentId = "build" };
        var input = new TuiInputEditorState();
        input.SetTextAndCursor("/mo", 3);

        var surface = new FakeTerminalSurface();
        var view = Renderer.BuildActiveView(
            state,
            input,
            100,
            completions: [new TuiCompletionItem("/model", "切换模型")]);

        var text = surface.RenderToString(view);

        text.Should().Contain("/model");
        text.IndexOf("/model", StringComparison.Ordinal)
            .Should().BeLessThan(text.IndexOf($"{TuiGlyphs.Prompt} /mo", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildUserEchoBlock_ShouldRenderPromptPrefixedTextWithoutLabel()
    {
        var block = TuiChatEngine.BuildUserEchoBlock("你是谁");
        block.Kind.Should().Be(TuiBlockKind.User);
        block.IsTerminal.Should().BeTrue();

        var surface = new FakeTerminalSurface();
        var rendered = surface.RenderToString(Renderer.BuildCommitted(block, 120));

        // `>` 前缀 + 内容；不出现旧的「你」标签行。
        rendered.Should().Contain($"{TuiGlyphs.Prompt} 你是谁");
        rendered.Should().NotContain("你 你是谁");
    }
}
