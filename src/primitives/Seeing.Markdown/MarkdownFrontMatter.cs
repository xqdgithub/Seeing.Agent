namespace Seeing.Markdown;

/// <summary>
/// YAML frontmatter 剥离。
/// <para>
/// 文档类 Markdown 常带 <c>---</c> 包裹的元数据头。直接交给 Markdig 时，
/// 闭合的 <c>---</c> 会被当成 setext 标题下划线，把整段元数据渲染成一个巨大标题
/// （用户看到 id/type/tags 满屏）。因此文档渲染必须先剥掉这一段。
/// </para>
/// <para>
/// <b>为什么不剥会话消息</b>：消息里开头的 <c>---</c> 通常是分隔线，
/// 当成元数据剥掉会丢内容。剥与不剥由调用方选入口区分（见 <see cref="MarkdownPipelineFactory"/>）。
/// </para>
/// </summary>
public static class MarkdownFrontMatter
{
    /// <summary>剥离 frontmatter；没有 frontmatter 时原样返回。</summary>
    /// <param name="content">原始 Markdown。</param>
    /// <returns>剥离后的正文；入参为 null/空或没有闭合分隔线时返回入参本身。</returns>
    public static string? Strip(string? content)
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
