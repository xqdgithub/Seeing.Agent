using FluentAssertions;
using Seeing.Markdown;
using Xunit;

namespace Seeing.Markdown.Tests;

/// <summary>
/// 管线工厂契约：两条入口（保留 / 剥离 frontmatter）共用同一份渲染管线。
/// <para>
/// 这类缺口编译器看不见——CSS 选择器与 HTML 输出都是静默的，
/// 因此按渲染产物断言。历史缺陷：FileViewer / MemoryDetailPage 曾用裸
/// <c>new MarkdownPipelineBuilder().Build()</c>，表格渲染成一堆竖线。
/// </para>
/// </summary>
public class MarkdownPipelineFactoryTests
{
    private const string Table = """
        | 工具名 | 描述 |
        |---|---|
        | read_file | 读取文件 |
        """;

    [Fact]
    public void Render_Table_ShouldProduceTableElement()
    {
        MarkdownPipelineFactory.Render(Table)
            .Should().Contain("<table>", "裸管线不会注册表格扩展，表格会退化成一堆竖线");
    }

    [Fact]
    public void Render_Table_ShouldWrapInScrollContainer()
    {
        var html = MarkdownPipelineFactory.Render(Table);

        html.Should().Contain("<div class=\"md-table-scroll\">");
        (html.Split(MarkdownTableExtension.ContainerClass).Length - 1)
            .Should().Be(1, "每个表格只包一层，重复包裹会产生嵌套滚动条");
    }

    [Fact]
    public void Render_MultipleTables_ShouldEachGetOwnContainer()
    {
        var html = MarkdownPipelineFactory.Render($"{Table}\n\n中间段落\n\n{Table}");

        (html.Split(MarkdownTableExtension.ContainerClass).Length - 1).Should().Be(2);
    }

    [Fact]
    public void Render_WithoutTable_ShouldNotEmitScrollContainer()
    {
        MarkdownPipelineFactory.Render("只有段落与 `内联代码`。")
            .Should().NotContain(MarkdownTableExtension.ContainerClass);
    }

    [Fact]
    public void Render_RawHtml_ShouldBeEscaped()
    {
        var html = MarkdownPipelineFactory.Render("<script>alert(1)</script>");

        html.Should().NotContain("<script>", "DisableHtml 是既有安全契约，不得因管线合并而放松");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Render_NullOrEmpty_ShouldReturnEmpty(string? content)
    {
        MarkdownPipelineFactory.Render(content).Should().Be(string.Empty);
        MarkdownPipelineFactory.RenderDocument(content).Should().Be(string.Empty);
    }

    [Fact]
    public void Render_DocumentWithFrontMatter_ShouldKeepItAsContent()
    {
        var html = MarkdownPipelineFactory.Render("---\ntitle: x\n---\n正文");

        html.Should().NotContain("<h2>", "会话消息入口不剥 frontmatter，--- 是分隔线不是 setext 标题");
        html.Should().Contain("title", "消息入口把元数据当普通正文保留");
    }

    [Fact]
    public void RenderDocument_WithFrontMatter_ShouldStripMetadata()
    {
        var html = MarkdownPipelineFactory.RenderDocument("---\ntitle: 记忆\nid: abc\n---\n正文");

        html.Should().NotContain("title: 记忆", "文档入口必须先剥 frontmatter");
        html.Should().NotContain("id: abc");
        html.Should().Contain("正文");
    }

    [Fact]
    public void RenderDocument_Table_ShouldAlsoWrapInScrollContainer()
    {
        MarkdownPipelineFactory.RenderDocument(Table)
            .Should().Contain("<div class=\"md-table-scroll\">", "两条入口共用同一管线");
    }
}
