using FluentAssertions;
using Seeing.Agent.Abstractions.Background;
using Seeing.Agent.WebUI.Models;

namespace Seeing.Agent.WebUI.Tests.Rendering;

/// <summary>
/// 前台活动（<c>bash</c> 未置 background）的呈现契约。
/// <para>
/// 仓库没有组件渲染基础设施，这里按源码断言关键分支（与
/// <see cref="BackgroundActivityCardContractTests"/> 同一模式）。核心约束：
/// 前台运行中不得显示后台 hint / 取消，只提供「转后台」；转后台后活动来源不变，
/// 只能靠工具调用是否结束切回后台样式。
/// </para>
/// </summary>
public class ForegroundActivityRenderingContractTests
{
    private const string BashComponent = "Components/Messaging/BashMessageComponent.razor";
    private const string ToolComponent = "Components/Messaging/ToolCallMessageComponent.razor";

    private const string BashHintGuard =
        "var showBackgroundHint = bg is not null";

    /// <summary>
    /// 前台运行中：hint 行必须被守卫挡住。
    /// </summary>
    [Fact]
    public void Foreground_ShouldNotRenderBackgroundHint()
    {
        var bash = ReadWebUiSource(BashComponent);

        bash.Should().Contain(BashHintGuard,
            "hint 的判定必须同时看来源与工具是否结束，不能只看 IsForeground");

        var hintIndex = bash.IndexOf("<div class=\"bash-background-hint\"", StringComparison.Ordinal);
        hintIndex.Should().BeGreaterThan(0, "后台仍要渲染这一行");

        var guardIndex = bash.LastIndexOf(
            "@if (bg is not null && showBackgroundHint)", hintIndex, StringComparison.Ordinal);
        guardIndex.Should().BeGreaterThan(0,
            "hint 行必须包在 showBackgroundHint 里；前台运行中不得出现「已转后台」这类说明");

        var tool = ReadWebUiSource(ToolComponent);

        tool.Should().Contain("!bg.IsForeground",
            "通用工具卡与 bash 卡必须用同一条判定");
        tool.Should().Contain("|| ToolCall.BackgroundDetached",
            "前台正常完成不等于转后台：只有强类型标记才允许出现后台提示");

        var headerCardIndex = tool.IndexOf(
            "Compact=\"true\" ShowOutput=\"false\"", StringComparison.Ordinal);
        headerCardIndex.Should().BeGreaterThan(0);

        var headerGuardIndex = tool.LastIndexOf(
            "@if (ShowBackgroundHint)", headerCardIndex, StringComparison.Ordinal);
        headerGuardIndex.Should().BeGreaterThan(0,
            "活动状态行也要被守卫挡住：工具卡头部已有状态与转圈，前台再补一行就是重复");
    }

    /// <summary>
    /// 前台运行中：可转后台时必须给出入口。
    /// </summary>
    [Fact]
    public void Foreground_ShouldOfferDetachButton()
    {
        var foregroundRunning = Activity(
            BackgroundActivityOrigin.ToolForeground,
            BackgroundActivityStatus.Running);
        var foregroundTerminal = Activity(
            BackgroundActivityOrigin.ToolForeground,
            BackgroundActivityStatus.Succeeded);
        var backgroundRunning = Activity(
            BackgroundActivityOrigin.ToolBackground,
            BackgroundActivityStatus.Running);

        foregroundRunning.CanDetach.Should().BeTrue("前台活动仍有等待者，可以放行");
        foregroundTerminal.CanDetach.Should().BeFalse("终态没有等待者");
        backgroundRunning.CanDetach.Should().BeFalse("后台活动没有前台等待者，无可放行");

        var bash = ReadWebUiSource(BashComponent);

        bash.Should().Contain("ToolCall.BackgroundActivity is { CanDetach: true }",
            "「转后台」在输出区操作条里，仅前台运行中可放行时出现");
        bash.Should().Contain("disabled=\"@_detaching\" @onclick=\"DetachAsync\">转后台</button>");
        bash.Should().Contain("await ActivityControl.DetachAsync(activity.Id);",
            "点击后交给控制面，不在本地改状态");

        var tool = ReadWebUiSource(ToolComponent);

        tool.Should().Contain("else if (activityHeader.CanDetach)");
        tool.Should().Contain("@onclick=\"DetachAsync\">转后台</button>");
        tool.Should().Contain("await ActivityControl.DetachAsync(activity.Id);");
    }

    /// <summary>
    /// 后台活动：维持现状——hint 行 + 取消，且不出现「转后台」。
    /// </summary>
    [Fact]
    public void Background_ShouldRenderHintRow()
    {
        var backgroundRunning = Activity(
            BackgroundActivityOrigin.ToolBackground,
            BackgroundActivityStatus.Running);

        backgroundRunning.IsForeground.Should().BeFalse();
        backgroundRunning.CanDetach.Should().BeFalse();

        var bash = ReadWebUiSource(BashComponent);

        // 守卫为「非前台 或 转过后台」：后台活动不论工具状态都显示 hint。
        bash.Should().Contain("!bg.IsForeground");
        bash.Should().Contain("ToolCall.BackgroundDetached", "转后台后工具已返回、活动仍在跑");
        bash.Should().Contain("class=\"bash-background-hint\"");
        bash.Should().Contain("bg.StatusText");
        bash.Should().Contain("bash-background-id");
        bash.Should().Contain("CancelBackgroundAsync", "后台取消入口保持原样");

        var tool = ReadWebUiSource(ToolComponent);

        tool.Should().Contain("Compact=\"true\" ShowOutput=\"false\"",
            "后台活动仍照常渲染状态行");
    }

    /// <summary>
    /// 转后台后：<c>Origin</c> 仍是 <c>ToolForeground</c>，工具已返回，
    /// 此时必须切回后台样式（hint 行 + 取消），且不再显示「转后台」。
    /// </summary>
    [Fact]
    public void AfterDetach_WhenToolFinished_ShouldRenderHintRow()
    {
        // 「转后台」只放行等待者，不改变活动来源：模拟工具返回后的同一活动。
        var detached = Activity(
            BackgroundActivityOrigin.ToolForeground,
            BackgroundActivityStatus.Running);

        detached.IsForeground.Should().BeTrue("转后台不修改活动的来源");
        detached.CanDetach.Should().BeTrue("活动本身仍在运行");

        var bash = ReadWebUiSource(BashComponent);

        bash.Should().Contain("ToolCall.BackgroundDetached",
            "不能用 IsForeground 单独判定：转后台后它仍为 true，但等待已经结束");
        bash.Should().Contain("private bool ShowBackgroundHint",
            "转后台后由 BackgroundDetached 判定，不再依赖按钮的 else 分支");
        bash.Should().Contain("class=\"bash-background-hint\"");

        var tool = ReadWebUiSource(ToolComponent);

        tool.Should().Contain("ToolCall.BackgroundDetached");
        tool.Should().Contain("else if (activityHeader.CanDetach)");
    }

    /// <summary>
    /// 「转后台」失败必须有可见反馈：忽略返回值时按钮点了没反应，用户只会反复点。
    /// </summary>
    [Fact]
    public void DetachFailure_ShouldRenderVisibleHint()
    {
        var bash = ReadWebUiSource(BashComponent);

        bash.Should().Contain("_detachFailed", "控制面返回 false 时必须留下本地失败标记");
        bash.Should().Contain("_detachFailed = !detached",
            "失败判据是控制面返回值，不是本地猜测的活动状态");
        bash.Should().Contain("bash-detach-error");
        bash.Should().Contain("转后台失败：活动可能已结束");

        var css = ReadWebUiSource("wwwroot/css/bash-terminal.css");
        css.Should().Contain(".bash-detach-error", "失败提示需要独立样式，不能和正常 hint 同色");
    }

    private static BackgroundActivityViewModel Activity(
        BackgroundActivityOrigin origin,
        BackgroundActivityStatus status)
        => BackgroundActivityViewModel.FromSnapshot(new BackgroundActivitySnapshot
        {
            Id = "act_0123456789abcdef0123456789abcdef",
            Kind = "process",
            Title = "命令",
            Origin = origin,
            WorkspaceRoot = ".",
            HostInstanceId = "host",
            OwnerSessionId = "s1",
            OwnerExecutionId = "e1",
            Status = status,
            Capabilities = BackgroundActivityCapabilities.CanCancel
                | BackgroundActivityCapabilities.CanStreamOutput
        });

    /// <summary>读取 WebUI 源码：测试运行时输出目录不含源码，需要回溯到仓库内的 samples 目录。</summary>
    private static string ReadWebUiSource(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(
                dir.FullName,
                "samples",
                "Seeing.Agent.WebUI",
                relativePath.Replace('/', Path.DirectorySeparatorChar));

            if (File.Exists(candidate))
                return File.ReadAllText(candidate);

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"找不到 WebUI 源文件：{relativePath}");
    }
}
