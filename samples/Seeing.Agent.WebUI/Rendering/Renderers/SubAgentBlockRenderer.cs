using Microsoft.AspNetCore.Components;
using Seeing.Agent.WebUI.Models.Messaging;
using Seeing.Agent.WebUI.Rendering.Abstractions;

namespace Seeing.Agent.WebUI.Rendering.Renderers;

/// <summary>
/// 子代理内容块渲染器
/// </summary>
public class SubAgentBlockRenderer : IContentBlockRenderer
{
    /// <inheritdoc/>
    public ContentBlockType BlockType => ContentBlockType.SubAgent;

    /// <inheritdoc/>
    public int Priority => 80;

    /// <inheritdoc/>
    public string Name => "SubAgent";

    /// <inheritdoc/>
    public RenderFragment Render(ContentBlock block, RenderContext context)
    {
        var agentName = block.Extensions?.TryGetValue("subAgentName", out var name) == true
            ? name?.ToString() ?? "SubAgent"
            : "SubAgent";
        var content = block.Content ?? string.Empty;
        var cacheKey = context.GetMarkdownCacheKey(block);
        var isStreaming = block.IsStreaming || context.IsStreaming;

        return builder =>
        {
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "class", "content-block-subagent");
            builder.AddAttribute(2, "style",
                "padding: var(--space-2); background: var(--color-bg-secondary); " +
                "border-radius: var(--radius-sm); margin: 4px 0; " +
                "border-left: 3px solid var(--color-primary);");

            // 头部
            builder.OpenElement(3, "div");
            builder.AddAttribute(4, "class", "subagent-header");
            builder.AddAttribute(5, "style",
                "display: flex; align-items: center; gap: var(--space-1); margin-bottom: var(--space-1);");

            // 机器人图标
            builder.OpenElement(6, "span");
            builder.AddAttribute(7, "style", "color: var(--color-primary); font-size: 16px;");
            builder.AddContent(8, "🤖");
            builder.CloseElement();

            // 代理名称
            builder.OpenElement(9, "span");
            builder.AddAttribute(10, "style", "font-weight: 500; font-size: 13px;");
            builder.AddContent(11, agentName);
            builder.CloseElement();

            // 流式动画
            if (isStreaming)
            {
                builder.OpenElement(12, "span");
                builder.AddAttribute(13, "style", "margin-left: var(--space-1);");
                builder.AddContent(14, "⏳");
                builder.CloseElement();
            }

            builder.CloseElement(); // header

            // 内容
            if (!string.IsNullOrEmpty(content))
            {
                builder.OpenElement(15, "div");
                builder.AddAttribute(16, "class", "subagent-content");
                builder.AddAttribute(17, "style",
                    "font-size: var(--font-size-sm); padding-left: var(--space-3);");

                var html = context.Cache?.GetOrCreateMarkdown(content, cacheKey) ?? content;
                builder.AddContent(18, new MarkupString(html));

                builder.CloseElement();
            }

            builder.CloseElement(); // container
        };
    }

    /// <inheritdoc/>
    public bool CanRender(ContentBlock block)
    {
        return block.Type == ContentBlockType.SubAgent;
    }
}
