using FluentAssertions;
using Seeing.Agent.Tui.Rendering;
using Seeing.Agent.Tui.Services;
using Spectre.Console;

namespace Seeing.Agent.Tui.Tests.Rendering;

/// <summary>
/// 状态栏计数接线测试：待批 / 后台执行计数经可选参数写作渲染文本。
/// </summary>
public sealed class StatusBarViewTests
{
    private static string Render(Spectre.Console.Rendering.IRenderable renderable)
    {
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(writer),
        });
        console.Profile.Width = 120;
        console.Write(renderable);
        return writer.ToString();
    }

    [Fact]
    public void Render_WithCounts_ShouldShowPendingAndBackground()
    {
        var state = new TuiViewState { SessionId = "ses_1", AgentId = "build" };

        var text = Render(StatusBarView.Render(state, 120, pendingApprovals: 2, backgroundExecutions: 3));

        text.Should().Contain("待批 2");
        text.Should().Contain("3 后台执行");
    }

    [Fact]
    public void Render_WithoutCounts_ShouldOmitCounters()
    {
        var state = new TuiViewState { SessionId = "ses_1", AgentId = "build" };

        var text = Render(StatusBarView.Render(state, 120));

        text.Should().NotContain("待批");
        text.Should().NotContain("后台执行");
    }

    [Fact]
    public void Render_WithHint_ShouldShowHint()
    {
        var state = new TuiViewState { SessionId = "ses_1", AgentId = "build", IsExecuting = true };

        var text = Render(StatusBarView.Render(state, 120, hint: "再按一次 Esc 取消执行"));

        text.Should().Contain("再按一次 Esc 取消执行");
    }
}
