using FluentAssertions;

namespace Seeing.Agent.WebUI.Tests.Infrastructure;

/// <summary>
/// SessionWindow Summary 摘要卡回归守卫：完整卡必须常显预览正文与来源行；
/// 侧栏摘要卡（含已完成紧凑卡）高度自适应内容（不写死 200px）。
/// </summary>
public class SessionWindowSummarySourceTests
{
    private static string RepoRoot => FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Seeing.Agent.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("无法定位仓库根目录（未找到 Seeing.Agent.slnx）");
    }

    private static string SessionWindowPath => Path.Combine(
        RepoRoot, "samples", "Seeing.Agent.WebUI", "Components", "SessionWindow.razor");

    private static string SessionPageCssPath => Path.Combine(
        RepoRoot, "samples", "Seeing.Agent.WebUI", "wwwroot", "css", "session-page.css");

    [Fact]
    public void SummaryCard_ShouldRenderPreviewBody()
    {
        var source = File.ReadAllText(SessionWindowPath);

        var start = source.IndexOf("conference-tile-body", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, "完整卡应存在预览正文容器");

        var bodyBlock = source[start..Math.Min(source.Length, start + 400)];
        bodyBlock.Should().Contain("GetLatestAssistantPreview()", "预览正文必须常显（三分支截断 120 字）");
    }

    [Fact]
    public void SummaryCard_ShouldRenderSourceLine()
    {
        var source = File.ReadAllText(SessionWindowPath);

        source.Should().Contain("conference-tile-parent", "完整卡来源行容器必须保留");
        source.Should().Contain("GetSourceLine()", "来源行必须渲染（§4.7）");
    }

    [Fact]
    public void CompactSummaryCard_ShouldRetainPreviewAndSource()
    {
        var source = File.ReadAllText(SessionWindowPath);

        var start = source.IndexOf("conference-tile--compact", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, "应存在已完成紧凑卡分支");

        var end = source.IndexOf("conference-tile-compact-meta", start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start, "紧凑卡应含预览摘要行");

        var compactBlock = source[start..(end + 400)];
        compactBlock.Should().Contain("GetCompactPreview()", "紧凑卡必须显示预览摘要（曾因挤压不可见）");
        compactBlock.Should().Contain("GetSourceLine()", "紧凑卡必须保留来源行（§4.7）");
    }

    [Fact]
    public void CompactSummaryCard_ShouldNotUseGrayReadonlyBackground()
    {
        var source = File.ReadAllText(SessionWindowPath);

        source.Should().NotContain("conference-tile--readonly", "子代理/已完成卡不再使用灰底");
    }

    [Fact]
    public void SidebarSummaryCard_ShouldBeAutoHeight()
    {
        var css = File.ReadAllText(SessionPageCssPath);

        var start = css.IndexOf(".session-side-area .session-window--summary", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, "侧栏摘要卡应有定高覆盖规则");

        var block = css[start..Math.Min(css.Length, start + 300)];
        block.Should().Contain("flex: 0 0 auto",
            "侧栏摘要卡高度应自适应内容（避免定高在卡片中间留大片空白）");
        css.Should().NotContain("flex: 0 0 200px", "侧栏摘要卡不得再写死 200px 定高");
    }

    [Fact]
    public void CompactSummaryCard_ShouldUseGrayBackground()
    {
        var css = File.ReadAllText(SessionPageCssPath);

        var start = css.IndexOf(".conference-tile--compact", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, "应存在紧凑卡样式");

        var block = css[start..Math.Min(css.Length, start + 300)];
        block.Should().Contain("background:", "完成态紧凑卡用浅灰底与执行中卡区分");
    }

    [Fact]
    public void SideSections_ShouldHaveDistinctAccentModifiers()
    {
        var css = File.ReadAllText(SessionPageCssPath);

        css.Should().Contain("session-side-section--MainLineHistory", "主线历史分区应有独立强调色");
        css.Should().Contain("session-side-section--Derived", "派生分区应有独立强调色");
        css.Should().Contain("session-side-section--Children", "子会话分区应有独立强调色");
    }
}
