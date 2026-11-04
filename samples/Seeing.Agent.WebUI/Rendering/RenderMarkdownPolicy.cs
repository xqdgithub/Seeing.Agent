using Seeing.Agent.WebUI.Models.Messaging;

namespace Seeing.Agent.WebUI.Rendering;

/// <summary>
/// Markdown 渲染策略：流式进行中的文本/思考块跳过 Markdown，按纯文本渲染。
/// <para>
/// 原因：流式期每次增量都会让 Markdown 全文重解析，成本随文本长度平方增长
/// （<see cref="Caching.MemoryRenderCache.GetOrCreateMarkdown"/> 在内容变化时全量 ToHtml）。
/// 而流式阶段用户读的是增量，不需要即时排版。消息完成后一次性解析。
/// </para>
/// <para>非文本块（工具/附件/图片等）不受影响，恒走原有渲染路径。</para>
/// </summary>
public static class RenderMarkdownPolicy
{
    /// <summary>
    /// 该内容块是否应走 Markdown 渲染。
    /// <para>流式中的文本/思考/子代理块返回 <c>false</c>（纯文本）；其余返回 <c>true</c>。</para>
    /// </summary>
    public static bool ShouldUseMarkdown(ContentBlock block)
    {
        if (block is null)
            return false;

        return block.Type switch
        {
            ContentBlockType.Text or ContentBlockType.Reasoning or ContentBlockType.SubAgent
                => !block.IsStreaming,
            _ => true
        };
    }
}
