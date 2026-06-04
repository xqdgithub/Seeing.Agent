using Microsoft.AspNetCore.Components;
using Seeing.Agent.WebUI.Models.Messaging;
using Seeing.Agent.WebUI.Rendering.Abstractions;

namespace Seeing.Agent.WebUI.Rendering.Renderers;

/// <summary>
/// 分隔线内容块渲染器
/// </summary>
/// <remarks>
/// 用于在 Loop 的多个步骤之间显示分隔线。
/// </remarks>
public class DividerBlockRenderer : IContentBlockRenderer
{
    /// <inheritdoc/>
    public ContentBlockType BlockType => ContentBlockType.Divider;

    /// <inheritdoc/>
    public int Priority => 200; // 低优先级，分隔线显示在最后

    /// <inheritdoc/>
    public string Name => "Divider";

    /// <inheritdoc/>
    public RenderFragment Render(ContentBlock block, RenderContext context)
    {
        var stepIndex = block.Extensions?.TryGetValue("stepIndex", out var idx) == true
            ? (int?)idx ?? 0
            : 0;

        return builder =>
        {
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "class", "content-block-divider");
            builder.AddAttribute(2, "style",
                "display: flex; align-items: center; gap: var(--space-2); " +
                "margin: var(--space-3) 0;");

            // 步骤标签
            builder.OpenElement(3, "div");
            builder.AddAttribute(4, "class", "step-badge");
            builder.AddAttribute(5, "style",
                "display: inline-flex; align-items: center; justify-content: center; " +
                "min-width: 60px; padding: 2px 8px; background: var(--color-primary); " +
                "color: white; border-radius: var(--radius-sm); font-size: 11px; font-weight: 500;");
            builder.AddContent(6, $"Step {stepIndex + 1}");
            builder.CloseElement();

            // 分隔线
            builder.OpenElement(7, "div");
            builder.AddAttribute(8, "class", "step-line");
            builder.AddAttribute(9, "style", "flex: 1; height: 1px; background: var(--color-border);");
            builder.CloseElement();

            builder.CloseElement();
        };
    }

    /// <inheritdoc/>
    public bool CanRender(ContentBlock block)
    {
        return block.Type == ContentBlockType.Divider;
    }
}
