using Seeing.Markdown;

namespace Seeing.Agent.WebUI.Files.Workbenches;

/// <summary>
/// Markdown 渲染（工作台预览用）：剥离 YAML frontmatter 后交给共享管线。
/// <para>
/// <b>为什么要剥 frontmatter</b>：文档类 Markdown 常带 <c>---</c> 包裹的元数据头，
/// Markdig 会把闭合的 <c>---</c> 当作 setext 标题下划线，把整段元数据渲染成一个巨大标题。
/// </para>
/// <para>渲染失败降级为转义纯文本，绝不抛到渲染器（那会整块工作台变空白）。</para>
/// <para>
/// 实际渲染委托给 <see cref="MarkdownPipelineFactory.RenderDocument"/>：管线口径必须与
/// 会话消息、文件预览、记忆详情一致，否则同一份内容在不同入口看到的排版不同。
/// </para>
/// </summary>
public static class MarkdownRenderer
{
    /// <summary>渲染为 HTML。</summary>
    /// <param name="content">原始 Markdown。</param>
    public static string Render(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return "<p>（空文件）</p>";

        try
        {
            return MarkdownPipelineFactory.RenderDocument(content);
        }
        catch
        {
            return "<pre>" + System.Net.WebUtility.HtmlEncode(content) + "</pre>";
        }
    }
}
