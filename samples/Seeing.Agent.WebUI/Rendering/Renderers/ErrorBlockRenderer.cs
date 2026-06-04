using Microsoft.AspNetCore.Components;
using Seeing.Agent.WebUI.Models.Messaging;
using Seeing.Agent.WebUI.Rendering.Abstractions;

namespace Seeing.Agent.WebUI.Rendering.Renderers;

/// <summary>
/// 错误内容块渲染器
/// </summary>
public class ErrorBlockRenderer : IContentBlockRenderer
{
    /// <inheritdoc/>
    public ContentBlockType BlockType => ContentBlockType.Error;

    /// <inheritdoc/>
    public int Priority => 100;

    /// <inheritdoc/>
    public string Name => "Error";

    /// <inheritdoc/>
    public RenderFragment Render(ContentBlock block, RenderContext context)
    {
        var error = block.Content ?? "发生错误";

        return builder =>
        {
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "class", "content-block-error");
            builder.AddAttribute(2, "style",
                "padding: var(--space-2) var(--space-3); background: var(--color-error-bg); " +
                "border: 1px solid var(--color-error-border); border-radius: var(--radius-sm); " +
                "margin: 4px 0; display: flex; align-items: flex-start; gap: var(--space-2);");

            // 错误图标
            builder.OpenElement(3, "span");
            builder.AddAttribute(4, "style", "color: var(--color-error); font-size: 16px; flex-shrink: 0;");
            builder.AddContent(5, "❌");
            builder.CloseElement();

            // 错误信息
            builder.OpenElement(6, "span");
            builder.AddAttribute(7, "style", "color: var(--color-error); font-size: var(--font-size-sm);");
            builder.AddContent(8, error);
            builder.CloseElement();

            builder.CloseElement();
        };
    }

    /// <inheritdoc/>
    public bool CanRender(ContentBlock block)
    {
        return block.Type == ContentBlockType.Error;
    }
}
