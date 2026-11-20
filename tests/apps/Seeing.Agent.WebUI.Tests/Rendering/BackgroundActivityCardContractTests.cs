using FluentAssertions;

namespace Seeing.Agent.WebUI.Tests.Rendering;

/// <summary>
/// 后台活动卡片的展示契约守门（按源码断言：项目没有组件渲染基础设施）。
/// <para>
/// 两个用户体验问题都在样式/模板层面，编译不会失败、常规逻辑测试也不会变红：
/// <list type="number">
/// <item>compact 模式嵌在工具卡片内，再渲染一次标题就变成「两条重复的标题」。</item>
/// <item>compact 模式自带边框，看起来像工具卡片里另开了一个框。</item>
/// </list>
/// </para>
/// </summary>
public class BackgroundActivityCardContractTests
{
    [Fact]
    public void Compact_ShouldNotRenderTitle()
    {
        var source = ReadWebUiSource("Components/Messaging/BackgroundActivityCard.razor");

        var titleIndex = source.IndexOf("<span class=\"bg-activity-title\"", StringComparison.Ordinal);
        titleIndex.Should().BeGreaterThan(0, "非 compact 的独立时间线条目仍要显示标题");

        var titleRegion = source[..titleIndex];

        // 标题前面必须紧邻 `@if (!Compact)` 守卫（中间只允许空行与注释）。
        titleRegion.Should().Contain("@if (!Compact)",
            "compact 模式必须跳过标题渲染：外层工具卡片已经显示过同一个命令描述");
    }

    [Fact]
    public void Compact_ShouldDropItsOwnFrame()
    {
        var source = ReadWebUiSource("Components/Messaging/BackgroundActivityCard.razor");

        var compactBlock = ExtractCssBlock(source, ".bg-activity-card.is-compact");

        compactBlock.Should().NotBeEmpty();
        compactBlock.Should().Contain("border: 0", "compact 模式只作为工具卡片内的一行状态，不能再画一个圆角框");
        compactBlock.Should().Contain("background: transparent");
    }

    [Fact]
    public void LiveOutput_ShouldRenderAsScrollablePre()
    {
        var source = ReadWebUiSource("Components/Messaging/BackgroundActivityCard.razor");

        source.Should().Contain("<pre class=\"bg-activity-output\">@activity.LiveOutputTail</pre>",
            "实时输出尾部必须原样按 pre 渲染（保留换行）");

        var outputBlock = ExtractCssBlock(source, ".bg-activity-output");

        outputBlock.Should().NotBeEmpty();
        outputBlock.Should().Contain("max-height: 168px");
        outputBlock.Should().Contain("overflow: auto");
        outputBlock.Should().Contain("white-space: pre-wrap");
        outputBlock.Should().Contain("word-break: break-all");
        outputBlock.Should().Contain("font-family: monospace");
    }

    [Fact]
    public void Segmented_ShouldNotRenderEmptyShellWhenBothSectionsOff()
    {
        var source = ReadWebUiSource("Components/Messaging/BackgroundActivityCard.razor");

        source.Should().Contain("(ShowHeader || ShowOutput)",
            "两段都不渲染时不能留下只有 margin 的空壳节点");

        var headerGuardIndex = source.IndexOf("@if (ShowHeader)", StringComparison.Ordinal);
        var headIndex = source.IndexOf("bg-activity-head", StringComparison.Ordinal);
        headerGuardIndex.Should().BeGreaterThan(0);
        headIndex.Should().BeGreaterThan(headerGuardIndex, "状态行整块必须包在 ShowHeader 里");

        source.Should().Contain("@if (ShowOutput && !string.IsNullOrEmpty(activity.LiveOutputTail))",
            "输出块由 ShowOutput 单独控制，才能被搬到命令之后");
    }

    /// <summary>
    /// bash 命令卡的渲染顺序契约：状态行在头部、实时输出在命令之后。
    /// <para>
    /// 拆分前输出块渲染在整卡最上方（命令还没出现就先看到输出），用户读到的顺序与终端直觉相反。
    /// </para>
    /// </summary>
    [Fact]
    public void BashCard_ShouldRenderStatusHeaderBeforeCommand_AndOutputAfterCommand()
    {
        var source = ReadWebUiSource("Components/Messaging/BashMessageComponent.razor");

        var headerOnlyIndex = source.IndexOf("ShowOutput=\"false\"", StringComparison.Ordinal);
        var commandIndex = source.IndexOf("bash-command", StringComparison.Ordinal);
        var outputOnlyIndex = source.IndexOf("ShowHeader=\"false\"", StringComparison.Ordinal);

        headerOnlyIndex.Should().BeGreaterThan(0, "命令卡要用只出状态行的实例");
        commandIndex.Should().BeGreaterThan(0);
        outputOnlyIndex.Should().BeGreaterThan(0, "命令卡要用只出输出块的实例");

        headerOnlyIndex.Should().BeLessThan(commandIndex,
            "状态行（执行中/输出大小/取消）必须留在卡片头部、命令之前");
        outputOnlyIndex.Should().BeGreaterThan(commandIndex,
            "实时输出块必须紧跟命令之后，不能再渲染到命令上方");
    }

    [Fact]
    public void ToolCallCard_ShouldRenderStatusHeaderBeforeOutput_AndOutputBeforeResult()
    {
        var source = ReadWebUiSource("Components/Messaging/ToolCallMessageComponent.razor");

        var headerOnlyIndex = source.IndexOf("ShowOutput=\"false\"", StringComparison.Ordinal);
        var outputOnlyIndex = source.IndexOf("ShowHeader=\"false\"", StringComparison.Ordinal);
        var resultSectionIndex = source.IndexOf("tool-call-section-title is-success", StringComparison.Ordinal);

        headerOnlyIndex.Should().BeGreaterThan(0);
        outputOnlyIndex.Should().BeGreaterThan(0);
        resultSectionIndex.Should().BeGreaterThan(0);

        headerOnlyIndex.Should().BeLessThan(outputOnlyIndex,
            "通用工具卡同样先出状态行、后出输出块");
        outputOnlyIndex.Should().BeLessThan(resultSectionIndex,
            "实时输出要排在「结果」段之前：结果终态在输出之后到达");
    }

    /// <summary>截出某个 CSS 选择器的声明块（以 <c>{</c> 到配对的 <c>}</c> 为界）。</summary>
    private static string ExtractCssBlock(string source, string selector)
    {
        var start = source.IndexOf(selector, StringComparison.Ordinal);
        if (start < 0)
            return string.Empty;

        var open = source.IndexOf('{', start);
        if (open < 0)
            return string.Empty;

        var close = source.IndexOf('}', open);
        return close < 0 ? source[open..] : source[open..close];
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
