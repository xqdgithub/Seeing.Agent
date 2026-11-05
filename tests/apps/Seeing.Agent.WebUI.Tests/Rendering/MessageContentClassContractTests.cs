using FluentAssertions;
using System.Text.RegularExpressions;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Rendering;

/// <summary>
/// 消息容器的类名契约守门。
/// <para>
/// 所有 Markdown 表格 / 段落 / 列表 / 引用块样式都挂在 <c>.message-content</c> 下
/// （见 <c>wwwroot/css/message-item.css</c>）。Markdown 一旦渲染进不带该类的容器，
/// 表格就既没有框线也没有单元格内边距，表现为「表格没有框线、列与列黏连」。
/// </para>
/// <para>
/// 这类缺口不会被编译或常规单元测试发现（CSS 选择器不命中是静默的），
/// 因此在此按源码断言容器契约。
/// </para>
/// </summary>
public class MessageContentClassContractTests
{
    [Theory]
    [InlineData("Components/Messaging/TextMessageComponent.razor")]
    [InlineData("Components/Messaging/ReasoningMessageComponent.razor")]
    public void MarkdownContainer_ShouldCarryMessageContentClass(string relativePath)
    {
        var source = ReadWebUiSource(relativePath);

        // 注意不能只写 Contain("message-content")：message-content--streaming 也含该子串，
        // 那样即使正文容器漏了 message-content 类，断言依然会通过（起不到守门作用）。
        var hasStandaloneClass = Regex.IsMatch(
            source,
            @"message-content(?![\w-])",
            RegexOptions.CultureInvariant);

        hasStandaloneClass.Should().BeTrue(
            $"{relativePath} 渲染 Markdown，容器必须带独立的 message-content 类，否则表格框线等样式静默失效");
    }

    /// <summary>正文表格的框线、内边距、表头底色都必须由 .message-content 作用域提供。</summary>
    [Fact]
    public void MessageContentTable_ShouldDefineBordersAndCellPadding()
    {
        var css = ReadWebUiSource("wwwroot/css/message-item.css");

        css.Should().Contain(".message-content table");
        css.Should().Contain(".message-content th,");
        css.Should().Contain(".message-content td");

        var cellBlock = ExtractRuleBlock(css, ".message-content th,");
        cellBlock.Should().Contain("border:", "单元格必须有框线");
        cellBlock.Should().Contain("padding:", "单元格必须有内边距，否则列内容黏连");

        var tableBlock = ExtractRuleBlock(css, ".message-content table");
        tableBlock.Should().Contain("border-collapse", "相邻单元格边框需合并，避免双线");
    }

    /// <summary>从 CSS 文本中取出以指定选择器开头的规则块（到配对的右花括号为止）。</summary>
    private static string ExtractRuleBlock(string css, string selector)
    {
        var start = css.IndexOf(selector, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"CSS 中应存在 {selector} 规则");

        var open = css.IndexOf('{', start);
        if (open < 0)
            return string.Empty;

        var depth = 0;
        for (var i = open; i < css.Length; i++)
        {
            if (css[i] == '{')
                depth++;
            else if (css[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return css[start..(i + 1)];
            }
        }

        return css[start..];
    }

    /// <summary>
    /// 读取 WebUI 源码。测试运行时输出目录不含源码，需要回溯到仓库内的 samples 目录。
    /// </summary>
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
