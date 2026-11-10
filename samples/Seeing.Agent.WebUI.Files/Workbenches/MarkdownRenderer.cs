using Markdig;

namespace Seeing.Agent.WebUI.Files.Workbenches;

/// <summary>
/// Markdown 渲染（工作台预览用）：剥离 YAML frontmatter 后交给 Markdig。
/// <para>
/// <b>为什么要剥 frontmatter</b>：文档类 Markdown 常带 <c>---</c> 包裹的元数据头，
/// Markdig 会把闭合的 <c>---</c> 当作 setext 标题下划线，把整段元数据渲染成一个巨大标题。
/// </para>
/// <para>渲染失败降级为转义纯文本，绝不抛到渲染器（那会整块工作台变空白）。</para>
/// </summary>
public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline s_pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    /// <summary>渲染为 HTML。</summary>
    /// <param name="content">原始 Markdown。</param>
    public static string Render(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return "<p>（空文件）</p>";

        var body = StripFrontMatter(content);
        if (string.IsNullOrWhiteSpace(body))
            body = content;

        try
        {
            return Markdown.ToHtml(body, s_pipeline);
        }
        catch
        {
            return "<pre>" + System.Net.WebUtility.HtmlEncode(body) + "</pre>";
        }
    }

    /// <summary>
    /// 剥离 YAML frontmatter（首行 <c>---</c> 到下一个 <c>---</c> 或 <c>...</c>）。
    /// <para>没有 frontmatter 时原样返回（不吞掉开头的分隔线）。</para>
    /// </summary>
    internal static string StripFrontMatter(string content)
    {
        if (string.IsNullOrEmpty(content))
            return content;

        var lines = content.Replace("\r\n", "\n").Split('\n');
        if (lines.Length < 2 || lines[0].Trim() != "---")
            return content;

        for (var i = 1; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed is "---" or "...")
                return string.Join('\n', lines[(i + 1)..]);
        }

        // 没有闭合分隔线：不是 frontmatter，按原文渲染
        return content;
    }
}
