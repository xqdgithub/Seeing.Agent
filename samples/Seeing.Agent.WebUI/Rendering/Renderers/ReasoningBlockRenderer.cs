using Microsoft.AspNetCore.Components;
using Seeing.Agent.WebUI.Components.Messaging;
using Seeing.Agent.WebUI.Models.Messaging;
using Seeing.Agent.WebUI.Rendering.Abstractions;

namespace Seeing.Agent.WebUI.Rendering.Renderers;

/// <summary>
/// 推理/思考过程内容块渲染器
/// </summary>
/// <remarks>
/// <para>
/// 委托 <see cref="ReasoningMessageComponent"/> 渲染可折叠的思考过程区域。
/// 折叠状态由组件的 Blazor 原生状态管理，不再依赖 JavaScript DOM 操作，
/// 避免了 Blazor 重新渲染时覆盖用户折叠操作的问题。
/// </para>
/// </remarks>
public class ReasoningBlockRenderer : IContentBlockRenderer
{
    /// <inheritdoc/>
    public ContentBlockType BlockType => ContentBlockType.Reasoning;

    /// <inheritdoc/>
    public int Priority => 10;

    /// <inheritdoc/>
    public string Name => "Reasoning";

    /// <inheritdoc/>
    public RenderFragment Render(ContentBlock block, RenderContext context)
    {
        var content = block.Content ?? string.Empty;
        var isComplete = block.IsComplete;

        return builder =>
        {
            if (!context.Options.ShowReasoning && isComplete)
            {
                builder.OpenElement(0, "div");
                builder.AddAttribute(1, "class", "reasoning-hidden-indicator");
                builder.AddAttribute(2, "style",
                    "padding: 4px 8px; background: #f0f0f0; " +
                    "border-radius: var(--radius-sm); margin: 4px 0; font-size: 12px; " +
                    "color: var(--color-text-secondary);");
                builder.AddContent(3, $"💡 思考过程 ({content.Length} 字符)");
                builder.CloseElement();
                return;
            }

            builder.SetKey(block.Id);
            builder.OpenComponent<ReasoningMessageComponent>(0);
            builder.AddAttribute(1, "Content", content);
            builder.AddAttribute(2, "Context", context);
            builder.AddAttribute(3, "Block", block);
            builder.CloseComponent();
        };
    }

    /// <inheritdoc/>
    public bool CanRender(ContentBlock block)
    {
        return block.Type == ContentBlockType.Reasoning;
    }
}
