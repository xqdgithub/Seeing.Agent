using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Seeing.Agent.WebUI.Models.Messaging;
using Seeing.Agent.WebUI.Rendering.Abstractions;

namespace Seeing.Agent.WebUI.Rendering.Renderers;

/// <summary>
/// 图片内容块渲染器
/// </summary>
public class ImageBlockRenderer : IContentBlockRenderer
{
    /// <inheritdoc/>
    public ContentBlockType BlockType => ContentBlockType.Image;

    /// <inheritdoc/>
    public int Priority => 60;

    /// <inheritdoc/>
    public string Name => "Image";

    /// <inheritdoc/>
    public RenderFragment Render(ContentBlock block, RenderContext context)
    {
        var attachment = block.Attachment;

        return builder =>
        {
            if (attachment == null)
            {
                RenderMissingImage(builder);
                return;
            }

            var displayUrl = attachment.GetDisplayUrl();
            if (string.IsNullOrEmpty(displayUrl))
            {
                RenderMissingImage(builder);
                return;
            }

            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "class", "content-block-image");
            builder.AddAttribute(2, "style", "margin: 4px 0;");

            builder.OpenElement(3, "img");
            builder.AddAttribute(4, "src", displayUrl);
            builder.AddAttribute(5, "alt", attachment.FileName ?? "图片");
            builder.AddAttribute(6, "style",
                "max-width: 100%; max-height: 300px; border-radius: var(--radius-sm); " +
                "object-fit: contain; cursor: pointer;");
            builder.AddAttribute(7, "loading", "lazy");
            builder.CloseElement();

            builder.CloseElement();
        };
    }

    private static void RenderMissingImage(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "div");
        builder.AddAttribute(1, "class", "content-block-image-missing");
        builder.AddAttribute(2, "style",
            "padding: var(--space-2); background: var(--color-bg-secondary); " +
            "border-radius: var(--radius-sm); color: var(--color-text-secondary); " +
            "font-size: var(--font-size-sm);");
        builder.AddContent(3, "🖼️ 图片加载失败");
        builder.CloseElement();
    }

    /// <inheritdoc/>
    public bool CanRender(ContentBlock block)
    {
        return block.Type == ContentBlockType.Image && block.Attachment != null;
    }
}
