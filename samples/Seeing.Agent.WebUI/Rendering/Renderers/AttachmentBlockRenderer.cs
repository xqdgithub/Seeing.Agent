using Microsoft.AspNetCore.Components;
using Seeing.Agent.WebUI.Models;
using Seeing.Agent.WebUI.Models.Messaging;
using Seeing.Agent.WebUI.Rendering.Abstractions;

namespace Seeing.Agent.WebUI.Rendering.Renderers;

/// <summary>
/// 附件内容块渲染器
/// </summary>
public class AttachmentBlockRenderer : IContentBlockRenderer
{
    /// <inheritdoc/>
    public ContentBlockType BlockType => ContentBlockType.Attachment;

    /// <inheritdoc/>
    public int Priority => 70;

    /// <inheritdoc/>
    public string Name => "Attachment";

    /// <inheritdoc/>
    public RenderFragment Render(ContentBlock block, RenderContext context)
    {
        var attachment = block.Attachment;

        return builder =>
        {
            if (attachment == null)
            {
                builder.OpenElement(0, "div");
                builder.AddAttribute(1, "class", "content-block-attachment-missing");
                builder.AddAttribute(2, "style",
                    "padding: var(--space-2); background: var(--color-bg-secondary); " +
                    "border-radius: var(--radius-sm); color: var(--color-text-secondary);");
                builder.AddContent(3, "[附件数据缺失]");
                builder.CloseElement();
                return;
            }

            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "class", "content-block-attachment");
            builder.AddAttribute(2, "style",
                "display: inline-flex; align-items: center; padding: var(--space-1) var(--space-2); " +
                "background: var(--color-bg-container); border-radius: var(--border-radius); " +
                "border: 1px solid var(--color-border); gap: var(--space-1); margin: 2px 0;");

            // 文件图标
            builder.OpenElement(3, "span");
            builder.AddAttribute(4, "style", "font-size: 16px;");
            builder.AddContent(5, GetFileIcon(attachment));
            builder.CloseElement();

            // 文件名
            builder.OpenElement(6, "span");
            builder.AddAttribute(7, "style", "font-size: var(--font-size-sm); color: var(--color-text);");
            builder.AddContent(8, attachment.FileName ?? "未命名文件");
            builder.CloseElement();

            // 文件大小（如果有）
            if (attachment.FileSize > 0)
            {
                builder.OpenElement(9, "span");
                builder.AddAttribute(10, "style",
                    "font-size: var(--font-size-xs); color: var(--color-text-secondary); margin-left: var(--space-1);");
                builder.AddContent(11, FormatFileSize(attachment.FileSize.Value));
                builder.CloseElement();
            }

            builder.CloseElement();
        };
    }

    private static string GetFileIcon(ContentPartViewModel attachment)
    {
        return attachment.GetIconType() switch
        {
            "pdf" => "📄",
            "word" => "📝",
            "excel" => "📊",
            "ppt" => "📽️",
            "text" => "📃",
            "archive" => "📦",
            "code" => "💻",
            _ => "📎"
        };
    }

    private static string FormatFileSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        int order = 0;
        double size = bytes;

        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }

        return $"{size:0.#} {sizes[order]}";
    }

    /// <inheritdoc/>
    public bool CanRender(ContentBlock block)
    {
        return block.Type == ContentBlockType.Attachment && block.Attachment != null;
    }
}
