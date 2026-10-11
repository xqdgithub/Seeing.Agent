using FluentAssertions;
using Seeing.Agent.Tui.Input;
using Seeing.Agent.Tui.Rendering;
using Seeing.Agent.Tui.Services;
using Seeing.Agent.Tui.Tests.Fakes;

namespace Seeing.Agent.Tui.Tests.Rendering;

/// <summary>
/// 排队中消息区渲染：位于输入区上边界之上、正式消息区之下，每条一行且不折行。
/// </summary>
public sealed class TuiRendererPendingBarTests
{
    private static readonly TuiRenderer Renderer = new(new TuiRenderOptions());

    private static string Render(TuiViewState state)
    {
        var surface = new FakeTerminalSurface();
        return surface.RenderToString(Renderer.BuildActiveView(state, new TuiInputEditorState(), 100));
    }

    [Fact]
    public void BuildActiveView_WithPendingMessages_ShouldRenderPendingBarAboveInput()
    {
        var state = new TuiViewState { SessionId = "s", AgentId = "build" };
        state.AddPending(new TuiPendingMessage { Id = "a", Text = "排队一" });
        state.AddPending(new TuiPendingMessage { Id = "b", Text = "排队二" });

        var text = Render(state);

        text.Should().Contain("排队中（2）");
        text.Should().Contain("[1] 排队一");
        text.Should().Contain("[2] 排队二");
        // 排队区位于输入分隔线之上（分隔线在输入行之前）。
        text.IndexOf("[1] 排队一", StringComparison.Ordinal)
            .Should().BeLessThan(text.IndexOf(TuiGlyphs.Prompt, StringComparison.Ordinal));
    }

    [Fact]
    public void BuildActiveView_WithoutPendingMessages_ShouldNotRenderPendingBar()
    {
        var state = new TuiViewState { SessionId = "s", AgentId = "build" };

        Render(state).Should().NotContain("排队中");
    }

    [Fact]
    public void BuildActiveView_PendingTextWithNewline_ShouldFlattenToSingleLine()
    {
        var state = new TuiViewState { SessionId = "s", AgentId = "build" };
        state.AddPending(new TuiPendingMessage { Id = "a", Text = "第一行\n第二行" });

        Render(state).Should().Contain("[1] 第一行 第二行");
    }
}
