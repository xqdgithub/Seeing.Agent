using FluentAssertions;
using Seeing.Markdown;
using Xunit;

namespace Seeing.Markdown.Tests;

/// <summary>
/// frontmatter 剥离契约：文档类 Markdown 的 <c>---</c> 元数据头必须被剥掉，
/// 否则 Markdig 会把闭合的 <c>---</c> 当成 setext 标题下划线，
/// 把整段元数据渲染成一个巨大的标题（用户看到 id/type/tags 满屏）。
/// </summary>
public class FrontMatterTests
{
    [Fact]
    public void Strip_WithClosedFrontMatter_ShouldReturnBody()
    {
        var input = "---\ntitle: 记忆\nid: abc\n---\n正文第一段";

        MarkdownFrontMatter.Strip(input).Should().Be("正文第一段");
    }

    [Fact]
    public void Strip_WithDotsTerminator_ShouldReturnBody()
    {
        var input = "---\ntitle: x\n...\n正文";

        MarkdownFrontMatter.Strip(input).Should().Be("正文");
    }

    [Fact]
    public void Strip_WithoutFrontMatter_ShouldReturnInputVerbatim()
    {
        var input = "# 标题\n\n正文\n\n---\n\n分节";

        MarkdownFrontMatter.Strip(input).Should().Be(input,
            "首行不是 --- 时不得吞掉正文里的分隔线");
    }

    [Fact]
    public void Strip_WithUnclosedFrontMatter_ShouldReturnInputVerbatim()
    {
        var input = "---\ntitle: x\n正文没闭合";

        MarkdownFrontMatter.Strip(input).Should().Be(input,
            "没有闭合分隔线就不是 frontmatter，按原文渲染");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Strip_WithNullOrEmpty_ShouldReturnSame(string? input)
    {
        MarkdownFrontMatter.Strip(input).Should().Be(input);
    }

    [Fact]
    public void Strip_WithCrLfFrontMatter_ShouldHandleWindowsLineEndings()
    {
        var input = "---\r\ntitle: x\r\n---\r\n正文";

        MarkdownFrontMatter.Strip(input).Should().Be("正文");
    }
}
