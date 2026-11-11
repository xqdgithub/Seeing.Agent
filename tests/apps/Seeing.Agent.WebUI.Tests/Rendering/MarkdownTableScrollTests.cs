using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.WebUI.Rendering.Caching;
using Seeing.Agent.WebUI.Rendering.MarkdownExtensions;

namespace Seeing.Agent.WebUI.Tests.Rendering;

/// <summary>
/// Markdown 表格横向滚动契约。
/// <para>
/// 多列表格（再加上单元格里的长内联代码）min-content 宽度会超过气泡宽度。
/// <c>&lt;table&gt;</c> 自己的 <c>overflow-x</c> 不裁剪自身内容，救不了；
/// 外层 <c>.message-list-container</c> 又是 <c>overflow-x: hidden</c>，
/// 于是右侧列被直接剪掉、也没有任何滚动入口 —— 用户「md 表格左右无法滚动，看不全多列数据」。
/// </para>
/// <para>
/// 这类缺口编译器与常规断言都看不见（HTML/CSS 都是静默的），因此在源码与产物两层各设一道契约。
/// </para>
/// </summary>
public class MarkdownTableScrollTests
{
    /// <summary>表格必须被包进横向滚动容器，且容器只出现一次（不能重复包裹）。</summary>
    [Fact]
    public void RenderedTable_ShouldBeWrappedInScrollContainer()
    {
        var html = Render("""
            | 工具名 | 描述 | 参数 |
            |---|---|---|
            | read_file | 读取文件 | `path: string` |
            """);

        html.Should().Contain($"<div class=\"{ScrollableTableExtension.ContainerClass}\">",
            "表格必须包在横向滚动容器里，否则右侧列会被外层 overflow-x:hidden 剪掉");
        html.Should().Contain("</table>\n</div>",
            "滚动容器必须在 </table> 之后闭合");

        var occurrences = html.Split(ScrollableTableExtension.ContainerClass).Length - 1;
        occurrences.Should().Be(1, "每个表格只应包一层，重复包裹会产生嵌套滚动条");
    }

    /// <summary>没有表格的内容不应凭空多出滚动容器（避免影响纯文本渲染）。</summary>
    [Fact]
    public void ContentWithoutTable_ShouldNotEmitScrollContainer()
    {
        var html = Render("只有段落与 `内联代码`，没有表格。");

        html.Should().NotContain(ScrollableTableExtension.ContainerClass);
    }

    /// <summary>同一段内容里的多个表格各自独立滚动（一个表格的宽度不能拖累另一个）。</summary>
    [Fact]
    public void MultipleTables_ShouldEachGetOwnContainer()
    {
        var html = Render("""
            | a | b |
            |---|---|
            | 1 | 2 |

            中间段落。

            | c | d |
            |---|---|
            | 3 | 4 |
            """);

        var occurrences = html.Split(ScrollableTableExtension.ContainerClass).Length - 1;
        occurrences.Should().Be(2);
    }

    /// <summary>
    /// 渲染管线与 CSS 必须两侧同源：容器类名若在 CSS 里改错、
    /// 或 CSS 忘了给 overflow-x，表格依然不可滚动且没有任何报错。
    /// </summary>
    [Fact]
    public void ScrollContainer_ShouldDeclareHorizontalOverflowInCss()
    {
        var css = ReadWebUiSource("wwwroot/css/message-item.css");
        var rule = ExtractRuleBlock(css, $".{ScrollableTableExtension.ContainerClass} {{");

        rule.Should().NotBeEmpty($"CSS 必须定义 .{ScrollableTableExtension.ContainerClass} 规则");
        rule.Should().Contain("overflow-x: auto", "容器必须是横向滚动承载者");
        rule.Should().Contain("max-width: 100%", "容器不得撑破气泡宽度");
        rule.Should().Contain("overscroll-behavior-x", "滚到底后不应把横向滚动链给外层，否则整页跟着左右抖");
    }

    /// <summary>
    /// 表格自身的 <c>width: 100%</c> 必须保留：窄表格要铺满气泡，
    /// 而不是因为外面多包了一层就缩成内容宽度（那会让所有表格都变窄）。
    /// </summary>
    [Fact]
    public void TableRule_ShouldStillFillWidth()
    {
        var css = ReadWebUiSource("wwwroot/css/message-item.css");
        var rule = ExtractRuleBlock(css, ".message-content table {");

        rule.Should().Contain("width: 100%",
            "宽表格由 min-content 撑开、窄表格仍要铺满；去掉 width:100% 会让所有表格缩水");
    }

    private static string Render(string markdown)
    {
        var cache = new MemoryRenderCache(NullLogger<MemoryRenderCache>.Instance);
        return cache.GetOrCreateMarkdown(markdown, $"test:{markdown.Length}:{Guid.NewGuid():N}");
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
