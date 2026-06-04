using Microsoft.AspNetCore.Components;
using Seeing.Agent.WebUI.Models;
using Seeing.Agent.WebUI.Models.Messaging;
using Seeing.Agent.WebUI.Rendering.Abstractions;

namespace Seeing.Agent.WebUI.Rendering.Renderers;

/// <summary>
/// 权限请求内容块渲染器
/// </summary>
public class PermissionBlockRenderer : IContentBlockRenderer
{
    /// <inheritdoc/>
    public ContentBlockType BlockType => ContentBlockType.Permission;

    /// <inheritdoc/>
    public int Priority => 90;

    /// <inheritdoc/>
    public string Name => "Permission";

    /// <inheritdoc/>
    public RenderFragment Render(ContentBlock block, RenderContext context)
    {
        var permission = block.Extensions?.TryGetValue("permission", out var permObj) == true
            && permObj is PermissionRequestViewModel perm
            ? perm
            : null;

        return builder =>
        {
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "class", "content-block-permission");
            builder.AddAttribute(2, "style",
                "padding: var(--space-3); background: var(--color-warning-bg); " +
                "border: 1px solid var(--color-warning-border); border-radius: var(--radius-sm); " +
                "margin: 4px 0;");

            // 头部
            builder.OpenElement(3, "div");
            builder.AddAttribute(4, "style",
                "display: flex; align-items: center; gap: var(--space-2); margin-bottom: var(--space-2);");

            // 问号图标
            builder.OpenElement(5, "span");
            builder.AddAttribute(6, "style", "color: var(--color-warning); font-size: 18px;");
            builder.AddContent(7, "❓");
            builder.CloseElement();

            // 标题
            builder.OpenElement(8, "span");
            builder.AddAttribute(9, "style", "font-weight: 500; color: var(--color-warning);");
            builder.AddContent(10, "需要权限确认");
            builder.CloseElement();

            builder.CloseElement(); // header

            // 权限详情
            if (permission != null)
            {
                builder.OpenElement(11, "div");
                builder.AddAttribute(12, "style",
                    "font-size: var(--font-size-sm); color: var(--color-text);");

                // 权限类型
                builder.OpenElement(13, "div");
                builder.AddAttribute(14, "style", "margin-bottom: var(--space-1);");
                builder.OpenElement(15, "strong");
                builder.AddContent(16, "类型: ");
                builder.CloseElement();
                builder.AddContent(17, permission.PermissionKind ?? "未知");
                builder.CloseElement();

                // 资源
                if (!string.IsNullOrEmpty(permission.Resource))
                {
                    builder.OpenElement(18, "div");
                    builder.AddAttribute(19, "style", "margin-bottom: var(--space-1);");
                    builder.OpenElement(20, "strong");
                    builder.AddContent(21, "资源: ");
                    builder.CloseElement();
                    builder.AddContent(22, permission.Resource);
                    builder.CloseElement();
                }

                // 消息
                if (!string.IsNullOrEmpty(permission.Message))
                {
                    builder.OpenElement(23, "div");
                    builder.AddAttribute(24, "style",
                        "margin-top: var(--space-2); padding: var(--space-2); " +
                        "background: var(--color-bg-secondary); border-radius: var(--radius-sm); " +
                        "color: var(--color-text-secondary);");
                    builder.AddContent(25, permission.Message);
                    builder.CloseElement();
                }

                builder.CloseElement(); // details
            }

            builder.CloseElement(); // container
        };
    }

    /// <inheritdoc/>
    public bool CanRender(ContentBlock block)
    {
        return block.Type == ContentBlockType.Permission;
    }
}
