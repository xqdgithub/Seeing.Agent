using FluentAssertions;
using Seeing.Agent.WebUI.Rendering.Caching;
using Seeing.Markdown;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Rendering;

/// <summary>
/// 同一份 Markdown 在不同面板里必须长得一样。
/// </summary>
/// <remarks>
/// <para>
/// <b>真实事故</b>：会话页的表格有框线、有横向滚动条，文件工作台的表格两者都没有 ——
/// 单元格贴身、列一多右侧直接被裁掉。用户实测报障：「文件工作台中的 md 渲染和会话的
/// md 渲染样式不同，主要是表格没有框线和横向滚动条，导致很难看」。
/// </para>
/// <para>
/// <b>成因</b>：表格样式是**按容器类名**写的（<c>.message-content table</c>），
/// 而同一个 Markdown 管线在三个地方被使用，容器类名各不相同
/// （<c>.message-content</c> / <c>.markdown-body</c> / <c>.file-markdown</c>）。
/// 每加一个展示面板就要记得「再补一份 CSS」，漏掉的那次不会有任何报错，
/// 只有真人在面板里看表格才发现。
/// </para>
/// <para>
/// <b>修复方向</b>：把样式锚在**管线自己产出的标记**上 —— 每个表格都被
/// <see cref="MarkdownTableExtension.ContainerClass"/> 包了一层，那个类名是单一来源，
/// 与容器无关。这些用例钉住「锚点必须是它，且不能退回按容器类名复制」。
/// </para>
/// </remarks>
public class MarkdownTableConsistencyTests
{
    /// <summary>渲染管线产出滚动容器：样式锚点的前提。</summary>
    [Fact]
    public void Pipeline_ShouldWrapEveryTableInScrollContainer()
    {
        var html = MarkdownPipelineFactory.RenderDocument("| a | b |\n|---|---|\n| 1 | 2 |");

        html.Should().Contain($"<div class=\"{MarkdownTableExtension.ContainerClass}\">",
            "所有面板共用的锚点就是这个容器；它缺失时任何 CSS 都挂不上");
    }

    /// <summary>
    /// 框线必须挂在共享容器上：这是「所有面板一致」的唯一保证。
    /// </summary>
    [Fact]
    public void TableBorders_ShouldBeAnchoredOnSharedContainer()
    {
        var css = ReadWebUiSource("wwwroot/css/message-item.css");
        var cls = MarkdownTableExtension.ContainerClass;

        // 单元格框线、内边距
        var cellRule = ExtractRuleBlock(css, $".{cls} th,");
        cellRule.Should().NotBeEmpty($"框线规则必须锚在 .{cls}（管线产出的容器）上，而不是某个面板的容器类名");
        cellRule.Should().Contain("border: 1px solid", "没有框线就是用户报的「表格很难看」");
        cellRule.Should().Contain("padding", "单元格贴身同样不可读");

        // 表头底色
        var headRule = ExtractRuleBlock(css, $".{cls} th {{");
        headRule.Should().Contain("background", "表头需要与正文区分");
    }

    /// <summary>
    /// 横向滚动必须挂在共享容器上。
    /// </summary>
    [Fact]
    public void HorizontalScroll_ShouldBeAnchoredOnSharedContainer()
    {
        var css = ReadWebUiSource("wwwroot/css/message-item.css");
        var rule = ExtractRuleBlock(css, $".{MarkdownTableExtension.ContainerClass} {{");

        rule.Should().Contain("overflow-x: auto",
            "列多的表格必须能横向滚动；文件工作台缺这条时右侧列被直接裁掉");
    }

    /// <summary>
    /// 反向守门：不得再出现「按面板容器类名写的表格框线」。
    /// </summary>
    /// <remarks>
    /// 复制一份看着无害，但它会让下一份继续被复制，而新面板永远不会自动获得样式。
    /// 这里显式禁止三个已知容器各自声明表格框线。
    /// </remarks>
    [Theory]
    [InlineData("wwwroot/css/message-item.css", ".message-content th,")]
    [InlineData("wwwroot/css/memory-page.css", ".markdown-body th,")]
    public void PanelContainer_ShouldNotDuplicateTableBorders(string cssFile, string selector)
    {
        var css = ReadWebUiSource(cssFile);

        ExtractRuleBlock(css, selector).Should().BeEmpty(
            "表格框线必须只定义在共享的 .md-table-scroll 上；" +
            "按面板容器复制会漏掉新面板（文件工作台就是这么漏的）");
    }

    /// <summary>文件工作台的 Markdown 容器必须能滚动（长文与宽表格的前提）。</summary>
    [Fact]
    public void FilesPanel_ShouldAllowMarkdownScrolling()
    {
        var css = ReadFilesPanelSource();
        var rule = ExtractRuleBlock(css, ".file-markdown {");

        rule.Should().NotBeEmpty("文件工作台必须为 Markdown 容器声明样式");
        rule.Should().Contain("overflow: auto", "宽表格与长代码行需要滚动入口");
    }

    /// <summary>文件工作台的代码块不应与行内代码共用同一套内边距（会出现两层灰）。</summary>
    [Fact]
    public void FilesPanel_ShouldSeparateInlineCodeFromBlock()
    {
        var css = ReadFilesPanelSource();

        ExtractRuleBlock(css, ".file-markdown code {").Should().NotBeEmpty();
        ExtractRuleBlock(css, ".file-markdown pre {").Should().NotBeEmpty();
        ExtractRuleBlock(css, ".file-markdown pre code {")
            .Should().Contain("background: transparent",
                "代码块内的 code 若仍带行内底色，会叠出两层灰");
    }

    // ── helpers ──

    private static string ExtractRuleBlock(string css, string selector)
    {
        var start = css.IndexOf(selector, StringComparison.Ordinal);
        if (start < 0)
            return string.Empty;

        var depth = 0;
        for (var i = css.IndexOf('{', start); i >= 0 && i < css.Length; i++)
        {
            if (css[i] == '{')
                depth++;
            else if (css[i] == '}' && --depth == 0)
                return css[start..(i + 1)];
        }

        return css[start..];
    }

    private static string ReadWebUiSource(string relativePath)
        => ReadFrom("Seeing.Agent.WebUI", relativePath);

    private static string ReadFilesPanelSource()
        => ReadFrom("Seeing.Agent.WebUI.Files", Path.Combine("wwwroot", "files-panel.css"));

    private static string ReadFrom(string project, string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(
                dir.FullName, "samples", project, relativePath.Replace('/', Path.DirectorySeparatorChar));

            if (File.Exists(candidate))
                return File.ReadAllText(candidate);

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"找不到源文件：{project}/{relativePath}");
    }
}
