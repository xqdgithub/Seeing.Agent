using FluentAssertions;
using Seeing.Agent.Tui.Rendering;
using Seeing.Agent.Tui.Services;
using Seeing.Session.Core;
using Spectre.Console;

namespace Seeing.Agent.Tui.Tests.Rendering;

/// <summary>
/// 状态栏渲染辅助测试：两行结构（工作目录 / 其余信息）、按宽度收窄、审批模式展示。
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

    private static string[] Lines(Spectre.Console.Rendering.IRenderable renderable)
        => Render(renderable)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .ToArray();

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

    [Fact]
    public void Render_WithWorkspace_ShouldUseSecondLineWithLeftRightLayout()
    {
        var state = new TuiViewState
        {
            SessionId = "ses_1",
            WorkspaceRoot = "D:\\Old_Project\\MyProjects\\Seeing.Agent",
            AgentId = "build",
            ModelId = "trip/qwen3.8-flash",
            Budget = new TuiBudget(12345, 200000),
        };

        var lines = Lines(StatusBarView.Render(state, 120));

        lines.Should().HaveCount(2);

        // 第 1 行：状态信息（不含路径/用量）。
        lines[0].Trim().Should().StartWith("build");
        lines[0].Should().Contain(" · trip/qwen3.8-flash · ");
        lines[0].Should().NotContain("D:\\Old_Project");

        // 第 2 行：左工作目录 + 右用量，各占两端。
        var second = lines[1];
        second.Should().Contain("D:\\Old_Project\\MyProjects\\Seeing.Agent");
        second.Should().Contain("12.3k/200k (6%)");
        second.Should().NotContain("上下文");
        second.TrimStart().Should().StartWith("D:\\Old_Project");
        second.TrimEnd().Should().EndWith("12.3k/200k (6%)");
    }

    [Fact]
    public void Render_WithoutWorkspace_WithBudget_ShouldRightAlignUsageOnSecondLine()
    {
        var state = new TuiViewState
        {
            SessionId = "ses_1",
            AgentId = "build",
            Budget = new TuiBudget(12345, 200000),
        };

        var lines = Lines(StatusBarView.Render(state, 60));

        lines.Should().HaveCount(2);
        lines[0].Trim().Should().StartWith("build");
        lines[1].TrimEnd().Should().EndWith("12.3k/200k (6%)");
        lines[1].TrimStart().Should().NotStartWith("build");
    }

    [Fact]
    public void Render_WithoutWorkspaceOrBudget_ShouldRenderSingleLine()
    {
        var state = new TuiViewState { SessionId = "ses_1", AgentId = "build" };

        var lines = Lines(StatusBarView.Render(state, 120));

        lines.Should().HaveCount(1);
        lines[0].Trim().Should().StartWith("build");
    }

    [Fact]
    public void Render_WithLongWorkspace_TwoLinesButNeverWraps()
    {
        var state = new TuiViewState
        {
            SessionId = "ses_1",
            WorkspaceRoot =
                "C:\\Users\\Administrator\\projects\\MyProjects\\Seeing.Agent\\src\\capabilities\\Seeing.Agent.Tools.Question",
            AgentId = "build",
            ModelId = "trip/qwen3.8-flash",
            Budget = new TuiBudget(12345, 200000),
        };

        var lines = Lines(StatusBarView.Render(state, 60));

        lines.Should().HaveCount(2);
        foreach (var line in lines)
            DisplayText.Width(line.TrimEnd()).Should().BeLessThanOrEqualTo(60);

        // 第 2 行：路径被中间省略，右侧用量完整保留。
        var second = lines[1];
        second.Should().Contain("…");
        second.TrimStart().Should().StartWith("C:\\");
        second.TrimEnd().Should().EndWith("12.3k/200k (6%)");
    }

    [Fact]
    public void Render_WithNarrowWidth_WhenWorkspaceDoesNotFit_ShouldKeepUsage()
    {
        var state = new TuiViewState
        {
            SessionId = "ses_1",
            WorkspaceRoot = "C:\\Users\\Administrator\\projects\\MyProjects\\Seeing.Agent",
            AgentId = "build",
            Budget = new TuiBudget(987, null),
        };

        var lines = Lines(StatusBarView.Render(state, 16));

        lines.Should().HaveCount(2);
        lines[0].TrimEnd().Should().EndWith("空闲");
        // 路径宽度不足，让位给右侧用量。
        lines[1].Should().NotContain("C:\\");
        lines[1].TrimEnd().Should().EndWith("987");
        foreach (var line in lines)
            DisplayText.Width(line.TrimEnd()).Should().BeLessThanOrEqualTo(16);
    }

    [Fact]
    public void Render_At80Columns_ShouldKeepWorkspaceVisible()
    {
        var state = new TuiViewState
        {
            SessionId = "ses_1",
            WorkspaceRoot = "D:\\Old_Project\\MyProjects\\Seeing.Agent",
            AgentId = "build",
            ModelId = "trip/qwen3.8-flash",
            Budget = new TuiBudget(12345, 200000),
        };

        var lines = Lines(StatusBarView.Render(state, 80));

        lines.Should().HaveCount(2);
        lines[1].Should().Contain("D:\\Old_Project\\MyProjects\\Seeing.Agent");
        lines[1].Should().Contain("12.3k/200k (6%)");
        foreach (var line in lines)
            DisplayText.Width(line.TrimEnd()).Should().BeLessThanOrEqualTo(80);
    }

    [Theory]
    [InlineData(SessionAutoApprove.FollowGlobal, false, "审批 确认(全局)")]
    [InlineData(SessionAutoApprove.FollowGlobal, true, "审批 自动(全局)")]
    [InlineData(SessionAutoApprove.Enabled, false, "审批 自动")]
    [InlineData(SessionAutoApprove.Disabled, true, "审批 确认")]
    public void Render_ShouldShowEffectiveApprovalMode(SessionAutoApprove mode, bool globalAuto, string expected)
    {
        var state = new TuiViewState
        {
            SessionId = "ses_1",
            AgentId = "build",
            AutoApprove = mode,
            GlobalAutoApprove = globalAuto,
        };

        var text = Render(StatusBarView.Render(state, 120));

        text.Should().Contain(expected);
    }

    [Fact]
    public void Render_WithBudget_ShouldShowContextUsage()
    {
        var state = new TuiViewState
        {
            SessionId = "ses_1",
            AgentId = "build",
            Budget = new TuiBudget(12345, 200000),
        };

        var text = Render(StatusBarView.Render(state, 120));

        text.Should().Contain("12.3k/200k (6%)");
    }

    [Fact]
    public void Render_WithBudgetWithoutLimit_ShouldShowCurrentOnly()
    {
        var state = new TuiViewState
        {
            SessionId = "ses_1",
            AgentId = "build",
            Budget = new TuiBudget(987, null),
        };

        var text = Render(StatusBarView.Render(state, 120));

        text.Should().Contain("987");
        text.Should().NotContain("/");
    }
}
