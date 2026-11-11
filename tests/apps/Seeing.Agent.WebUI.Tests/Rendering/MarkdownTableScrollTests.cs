using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.WebUI.Rendering.Caching;
using Seeing.Markdown;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Rendering;

/// <summary>
/// 会话消息 Markdown 表格的样式契约。
/// <para>
/// 渲染行为（表格元素、滚动容器包裹、frontmatter 差异）由
/// <c>Seeing.Markdown.Tests</c> 的管线工厂用例守门；此处只保留两件只有宿主知道的事：
/// ① 缓存层确实走共享管线（否则表格会静默退化）；
/// ② 宿主 CSS 为 <c>.md-table-scroll</c> 声明了横向滚动（类名对不上时静默失效）。
/// </para>
/// </summary>
public class MarkdownTableScrollTests
{
    /// <summary>缓存层必须走共享管线，否则表格退化成普通段落且没有任何报错。</summary>
    [Fact]
    public void CacheRender_ShouldUseSharedFactory()
    {
        var cache = new MemoryRenderCache(NullLogger<MemoryRenderCache>.Instance);

        var html = cache.GetOrCreateMarkdown("| a | b |\n|---|---|\n| 1 | 2 |", "test:k1");

        html.Should().Contain("<table>", "缓存层必须走共享管线，否则表格退化成一堆竖线");
        html.Should().Contain($"<div class=\"{MarkdownTableExtension.ContainerClass}\">");
    }

    /// <summary>
    /// 渲染管线与 CSS 必须两侧同源：容器类名若在 CSS 里改错、
    /// 或 CSS 忘了给 overflow-x，表格依然不可滚动且没有任何报错。
    /// </summary>
    [Fact]
    public void ScrollContainer_ShouldDeclareHorizontalOverflowInCss()
    {
        var css = ReadWebUiSource("wwwroot/css/message-item.css");
        var rule = ExtractRuleBlock(css, $".{MarkdownTableExtension.ContainerClass} {{");

        rule.Should().NotBeEmpty($"CSS 必须定义 .{MarkdownTableExtension.ContainerClass} 规则");
        rule.Should().Contain("overflow-x: auto", "容器必须是横向滚动承载者");
        rule.Should().Contain("max-width: 100%", "容器不得撑破气泡宽度");
        rule.Should().Contain("overscroll-behavior-x", "滚到底后不应把横向滚动链给外层，否则整页跟着左右抖");
    }

    /// <summary>
    /// 表格自身的 <c>width: 100%</c> 必须保留：窄表格要铺满气泡，
    /// 而不是因为外面多包了一层就缩成内容宽度（那会让所有表格都变窄）。
    /// </summary>
    /// <remarks>
    /// 断言锚点已从 <c>.message-content table</c> 迁到 <c>.md-table-scroll table</c> ——
    /// 后者是 Markdown 管线自己产出的容器，所有面板共用；前者只覆盖会话页，
    /// 文件工作台正因为漏了这一份才没有框线与横向滚动。
    /// </remarks>
    [Fact]
    public void TableRule_ShouldStillFillWidth()
    {
        var css = ReadWebUiSource("wwwroot/css/message-item.css");
        var rule = ExtractRuleBlock(css, $".{MarkdownTableExtension.ContainerClass} table {{");

        rule.Should().Contain("width: 100%",
            "宽表格由 min-content 撑开、窄表格仍要铺满；去掉 width:100% 会让所有表格缩水");
    }

    private static string ExtractRuleBlock(string css, string selector)
    {
        var start = css.IndexOf(selector, StringComparison.Ordinal);
        if (start < 0)
            return string.Empty;

        var depth = 0;
        for (var i = css.IndexOf('{', start); i < css.Length; i++)
        {
            if (css[i] == '{')
                depth++;
            else if (css[i] == '}' && --depth == 0)
                return css[start..(i + 1)];
        }

        return css[start..];
    }

    private static string ReadWebUiSource(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(
                dir.FullName, "samples", "Seeing.Agent.WebUI", relativePath.Replace('/', Path.DirectorySeparatorChar));

            if (File.Exists(candidate))
                return File.ReadAllText(candidate);

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"找不到 WebUI 源文件：{relativePath}");
    }
}
