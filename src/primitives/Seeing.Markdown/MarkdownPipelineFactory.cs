using Markdig;

namespace Seeing.Markdown;

/// <summary>
/// Markdown 渲染管线的<b>唯一构造点</b>。
/// <para>
/// 历史上仓库里有 4 条独立管线，其中两条用的是裸 <c>new MarkdownPipelineBuilder().Build()</c>
/// （不注册表格扩展 → 表格渲染成一堆竖线）。收敛到一处后，所有渲染路径看到的
/// 语法支持与安全策略完全一致。
/// </para>
/// <para>
/// 两个入口的差别只有 frontmatter：会话消息里开头的 <c>---</c> 是分隔线（必须保留），
/// 文档 / 记忆文件里的 <c>---</c> 是元数据头（必须先剥，否则被当成 setext 标题下划线）。
/// </para>
/// </summary>
public static class MarkdownPipelineFactory
{
    /// <summary>
    /// 共享管线实例（不可变，线程安全）。
    /// <para>
    /// <c>DisableHtml</c> 是既有安全契约：禁止原始 HTML 透传，防注入。
    /// <see cref="MarkdownTableExtension"/> 必须排在 <c>UsePipeTables</c> / <c>UseGridTables</c>
    /// 之后，否则会被它们注册的默认表格渲染器顶掉。
    /// </para>
    /// </summary>
    public static MarkdownPipeline Pipeline { get; } = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UseAutoLinks()
        .UseTaskLists()
        .UsePipeTables()
        .UseGridTables()
        .UseListExtras()
        .UseEmphasisExtras()
        .UseAutoIdentifiers()
        .UseCitations()
        .UseCustomContainers()
        .UseFooters()
        .UseFootnotes()
        .UseMathematics()
        .UseSmartyPants()
        .Use<MarkdownTableExtension>()
        .Build();

    /// <summary>
    /// 渲染普通 Markdown 内容（会话消息、工具输出等）。frontmatter 原样保留。
    /// </summary>
    /// <param name="content">原始 Markdown。</param>
    /// <returns>渲染后的 HTML；入参为 null/空时返回空串。</returns>
    public static string Render(string? content)
        => string.IsNullOrEmpty(content)
            ? string.Empty
            : global::Markdig.Markdown.ToHtml(content, Pipeline);

    /// <summary>
    /// 渲染文档（文件预览 / 记忆详情 / 工作台）。先剥离 YAML frontmatter 再渲染。
    /// </summary>
    /// <param name="content">原始 Markdown。</param>
    /// <returns>渲染后的 HTML；入参为 null/空时返回空串。</returns>
    public static string RenderDocument(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return string.Empty;

        var body = MarkdownFrontMatter.Strip(content);
        return global::Markdig.Markdown.ToHtml(string.IsNullOrEmpty(body) ? content : body, Pipeline);
    }
}
