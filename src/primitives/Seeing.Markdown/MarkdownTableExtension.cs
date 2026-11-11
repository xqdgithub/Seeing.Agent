using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Renderers;
using Markdig.Renderers.Html;

namespace Seeing.Markdown;

/// <summary>
/// 让 Markdown 表格横向可滚动。
/// <para>
/// <b>根因</b>：表格列多、单元格里又有较长的内联代码时，表格的 min-content 宽度会超过容器宽度，
/// 表格随之溢出。<c>&lt;table&gt;</c> 自身的 <c>overflow-x</c> 不裁剪自己的内容，救不了；
/// 而会话消息的最外层滚动容器 <c>.message-list-container</c> 是 <c>overflow-x: hidden</c>
/// （横向滚动由页面布局独占），于是溢出部分被直接剪掉——右侧列看不见，也没有任何滚动入口。
/// </para>
/// <para>
/// <b>做法</b>：渲染时给每个表格套一层 <c>div.md-table-scroll</c>，由这层容器承担横向滚动，
/// 表格宽度与列宽算法完全不变（放得下就 100% 铺满，放不下就按 min-content 展开并出现滚动条）。
/// 样式见宿主 CSS（会话消息在 <c>wwwroot/css/message-item.css</c>）；
/// 容器类名由 <see cref="ContainerClass"/> 单一来源提供，CSS 与契约测试共用。
/// </para>
/// </summary>
public sealed class MarkdownTableExtension : IMarkdownExtension
{
    /// <summary>横向滚动容器的类名（CSS 与契约测试共用同一常量）。</summary>
    public const string ContainerClass = "md-table-scroll";

    /// <inheritdoc/>
    public void Setup(MarkdownPipelineBuilder pipelineBuilder)
    {
        // 只换渲染器，不引入新语法
    }

    /// <inheritdoc/>
    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
        if (renderer is not HtmlRenderer html)
            return;

        // 必须在 UsePipeTables/UseGridTables 之后 Setup，否则会被它们注册的默认渲染器顶掉
        html.ObjectRenderers.ReplaceOrAdd<HtmlTableRenderer>(new ScrollableTableRenderer());
    }

    /// <summary>在默认的表格 HTML 外再包一层横向滚动容器。</summary>
    private sealed class ScrollableTableRenderer : HtmlTableRenderer
    {
        protected override void Write(HtmlRenderer renderer, Table table)
        {
            renderer.Write("<div class=\"").Write(ContainerClass).Write("\">");
            base.Write(renderer, table);
            renderer.Write("</div>");
        }
    }
}
