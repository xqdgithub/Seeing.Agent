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
    /// 通用工具卡的渲染顺序契约：状态行 → 实时输出块 → 结果段。
    /// </summary>
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

    /// <summary>
    /// 后台 bash 卡片的输出必须复用前台终端 <c>pre</c> 样式，内容取活动实时输出。
    /// <para>
    /// 后台模式此前把 <c>BackgroundActivityCard</c> 的输出块嵌进来（另一套底色/边框/固定高度），
    /// 与前台命令卡看起来像两种东西；现在统一成前台的 <c>bash-terminal-pre</c>。
    /// </para>
    /// </summary>
    [Fact]
    public void BashBackground_ShouldUseTerminalPreForOutput()
    {
        var source = ReadWebUiSource("Components/Messaging/BashMessageComponent.razor");

        source.Should().Contain("bg is not null ? bg.LiveOutputTail : _card.DisplayOutput",
            "后台模式要优先取活动实时输出；工具结果只是一段「已转交」回执，不是命令输出");

        source.Should().Contain("class=\"bash-terminal-pre @(_outputExpanded ? null : \"is-collapsed\")\"",
            "后台输出必须落在前台同款终端 pre 上，颜色/字体/滚动行为才与前台一致");

        source.Should().NotContain("bg-activity-output",
            "后台卡片不再复用活动卡的输出块样式，否则又是两套外观");
    }

    /// <summary>
    /// 后台 bash 的结果说明（已转交 / activity_id / 跟踪工具提示）压成底部一行小字，
    /// 不能整段出现在输出区。
    /// </summary>
    [Fact]
    public void BashBackground_ShouldRenderHintRowInsteadOfFullResult()
    {
        var source = ReadWebUiSource("Components/Messaging/BashMessageComponent.razor");

        source.Should().Contain("class=\"bash-background-hint\"",
            "活动状态 / 输出大小 / 活动 id / 取消要收口成一行");

        var hintIndex = source.IndexOf("class=\"bash-background-hint\"", StringComparison.Ordinal);
        var hintEnd = source.IndexOf("</div>", hintIndex, StringComparison.Ordinal);
        hintEnd.Should().BeGreaterThan(hintIndex, "小字行必须是一个闭合的 div");

        var hintRegion = source[hintIndex..hintEnd];

        hintRegion.Should().Contain("bg.StatusText", "状态行改用一行文本，不再占一整块");
        hintRegion.Should().Contain("bash-background-id", "活动 id 仍要可见（便于对照 activity_status）");
        hintRegion.Should().Contain("CancelBackgroundAsync", "取消入口随小字行一起下沉");

        source.Should().NotContain("@_card.DisplayOutput</pre>",
            "后台模式的整段回执不得作为输出正文渲染");
    }

    /// <summary>
    /// 后台 bash 卡片不再嵌入活动卡：状态行与输出块都内联，出现两个实例就是重复渲染。
    /// </summary>
    [Fact]
    public void BashBackground_ShouldNotRenderDuplicateActivityCard()
    {
        var source = ReadWebUiSource("Components/Messaging/BashMessageComponent.razor");

        var instances = source.Split("BackgroundActivityCard").Length - 1;
        instances.Should().Be(0,
            "命令卡自己渲染命令/输出/状态行；再嵌活动卡就会出现两条重复的状态或输出");
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
