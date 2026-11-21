using FluentAssertions;

namespace Seeing.Agent.WebUI.Tests.Rendering;

/// <summary>
/// 「输出源」契约：前台正常完成必须用完整工具结果，转后台/真后台才用活动实时尾部。
/// <para>
/// 实测 bug：<c>echo a; echo b; echo c</c> 完成后卡片只剩第一行。原因是前台正常完成与转后台
/// 在活动层完全同形（<c>Origin=ToolForeground</c> + 工具已结束），旧实现用
/// 「活动仍在跑 且 工具已结束」猜「转过后台」，把 1 秒节流的 <c>LiveOutputTail</c>
/// 盖在完整的 <c>ToolResult</c> 上。现在唯一判据是 <c>BackgroundDetached</c>。
/// </para>
/// <para>
/// 仓库没有组件渲染基础设施，这里按源码断言关键分支（与
/// <see cref="ForegroundActivityRenderingContractTests"/> 同一模式）。
/// </para>
/// </summary>
public class BackgroundDetachedRenderingContractTests
{
    private const string BashComponent = "Components/Messaging/BashMessageComponent.razor";
    private const string ToolComponent = "Components/Messaging/ToolCallMessageComponent.razor";

    [Fact]
    public void ForegroundCompleted_ShouldUseToolResult_NotActivityTail()
    {
        var bash = ReadWebUiSource(BashComponent);

        bash.Should().Contain(
            "var liveTail = UsesActivityOutput ? bg!.LiveOutputTail : _card.DisplayOutput;",
            "输出源必须是二选一：活动尾部 或 完整工具结果");

        // 判定里必须出现「前台已结束且未转后台 → 不用活动输出」的分支。
        bash.Should().Contain("private bool UsesActivityOutput =>",
            "输出源只允许一处判定，避免模板里再猜一次");
        bash.Should().Contain("!bg.IsForeground || ToolCall.BackgroundDetached",
            "前台正常完成（未转后台）不得取活动尾部");
        bash.Should().Contain("ToolCall.IsRunning",
            "前台运行中的实时输出仍要来自活动尾部，否则终端在命令结束前一片空白");

        // 模板层只渲染 outputText（由 UsesActivityOutput 算好），不得再直接读活动尾部。
        bash.Should().NotContain("@bg.LiveOutputTail", "模板里再取一次活动尾部就会绕开输出源判定");

        var tool = ReadWebUiSource(ToolComponent);

        tool.Should().Contain(
            "@if (ToolCall.BackgroundActivity is { } activityOutput",
            "通用工具卡在前台正常完成时只渲染「结果」段");
        tool.Should().Contain(
            "&& UsesActivityOutput)",
            "活动尾部输出块必须挂在输出源判定上，否则前台正常完成会多渲染一份节流尾部");
        tool.Should().Contain("private bool UsesActivityOutput =>");
    }

    [Fact]
    public void Detached_ShouldUseActivityTail_AndShowHint()
    {
        var bash = ReadWebUiSource(BashComponent);

        bash.Should().Contain("var showBackgroundHint = bg is not null",
            "hint 的判定不能只看 IsForeground");
        bash.Should().Contain("&& (!bg.IsForeground || ToolCall.BackgroundDetached);",
            "转后台后 Origin 仍是 ToolForeground，只能看强类型标记");
        bash.Should().Contain("private bool UsesActivityOutput =>");
        bash.Should().Contain("ToolCall.BackgroundDetached",
            "转后台是取活动尾部的唯一依据");

        var tool = ReadWebUiSource(ToolComponent);

        tool.Should().Contain("&& (!bg.IsForeground || ToolCall.BackgroundDetached);",
            "通用工具卡与 bash 卡必须用同一条判定");
    }

    [Fact]
    public void Background_ShouldShowHint()
    {
        var bash = ReadWebUiSource(BashComponent);

        // 真后台（background=true）：Origin=ToolBackground，无需 detached 也显示 hint / 用活动输出。
        bash.Should().Contain("!bg.IsForeground", "后台活动不论工具状态都显示 hint");
        bash.Should().Contain("class=\"bash-background-hint\"");
        bash.Should().Contain("bg.StatusText");
        bash.Should().Contain("CancelBackgroundAsync", "后台取消入口保持原样");

        var tool = ReadWebUiSource(ToolComponent);

        tool.Should().Contain("!bg.IsForeground");
        tool.Should().Contain("Compact=\"true\" ShowOutput=\"false\"",
            "后台活动仍照常渲染状态行");
    }

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
