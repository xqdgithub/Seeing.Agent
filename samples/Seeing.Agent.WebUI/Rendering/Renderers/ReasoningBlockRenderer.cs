using Microsoft.AspNetCore.Components;
using Seeing.Agent.WebUI.Models.Messaging;
using Seeing.Agent.WebUI.Rendering.Abstractions;

namespace Seeing.Agent.WebUI.Rendering.Renderers;

/// <summary>
/// 推理/思考过程内容块渲染器
/// </summary>
/// <remarks>
/// <para>
/// 渲染可折叠的思考过程区域，支持：
/// <list type="bullet">
///   <item><description>流式输出时自动展开</description></item>
///   <item><description>完成后自动折叠</description></item>
///   <item><description>用户可手动折叠/展开</description></item>
///   <item><description>显示状态标签（思考中/字符数）</description></item>
/// </list>
/// </para>
/// <para>
/// ⚠️ <strong>线程安全要求：</strong>
/// 此类是无状态的，折叠状态由前端 JavaScript 管理。
/// </para>
/// </remarks>
public class ReasoningBlockRenderer : IContentBlockRenderer
{
    /// <inheritdoc/>
    public ContentBlockType BlockType => ContentBlockType.Reasoning;

    /// <inheritdoc/>
    public int Priority => 10; // 高优先级，推理块显示在最前

    /// <inheritdoc/>
    public string Name => "Reasoning";

    /// <inheritdoc/>
    public RenderFragment Render(ContentBlock block, RenderContext context)
    {
        // ✅ 捕获不可变数据
        var content = block.Content ?? string.Empty;
        var cacheKey = context.GetMarkdownCacheKey(block);
        // 优先使用 block 自身的状态，只有当 block 未完成时才使用 context 的流式状态
        var isComplete = block.IsComplete;
        var isStreaming = !isComplete && (block.IsStreaming || context.IsStreaming);
        var showReasoning = context.Options.ShowReasoning;
        var blockId = block.Id;

        return builder =>
        {
            // 如果配置隐藏推理过程，只显示简短提示
            if (!showReasoning && isComplete)
            {
                builder.OpenElement(0, "div");
                builder.AddAttribute(1, "class", "reasoning-hidden-indicator");
                builder.AddAttribute(2, "style",
                    "padding: 4px 8px; background: var(--color-bg-secondary); " +
                    "border-radius: var(--radius-sm); margin: 4px 0; font-size: 12px; " +
                    "color: var(--color-text-secondary);");
                builder.AddContent(3, $"💡 思考过程 ({content.Length} 字符)");
                builder.CloseElement();
                return;
            }

            // 流式输出时展开，完成后自动折叠
            var initialState = isStreaming ? "expanded" : "collapsed";

            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "class", $"content-block-reasoning reasoning-section {initialState}");
            builder.AddAttribute(2, "data-reasoning-id", blockId);

            // 折叠头部（可点击）
            builder.OpenElement(3, "div");
            builder.AddAttribute(4, "class", "reasoning-header");
            builder.AddAttribute(5, "onclick", $"toggleReasoning('{blockId}')");
            builder.AddAttribute(6, "style",
                "display: flex; align-items: center; padding: 4px 8px; " +
                "background: var(--color-bg-secondary); border-radius: var(--radius-sm); " +
                "margin: 4px 0; cursor: pointer; user-select: none;");

            // 展开/折叠图标
            builder.OpenElement(7, "span");
            builder.AddAttribute(8, "class", "reasoning-toggle-icon");
            builder.AddAttribute(9, "style",
                "margin-right: 4px; font-size: 12px; transition: transform 0.2s; " +
                "display: inline-block;");
            builder.AddContent(10, "▶");
            builder.CloseElement();

            // 状态图标
            var iconStyle = isStreaming
                ? "color: var(--color-primary); font-size: 14px; animation: spin 1s linear infinite;"
                : "color: var(--color-success); font-size: 14px;";
            builder.OpenElement(11, "span");
            builder.AddAttribute(12, "style", iconStyle);
            builder.AddContent(13, isStreaming ? "🔄" : "✅");
            builder.CloseElement();

            // 标题
            builder.OpenElement(14, "span");
            builder.AddAttribute(15, "style", "margin-left: 6px; font-weight: 500; font-size: 13px;");
            builder.AddContent(16, "思考过程");
            builder.CloseElement();

            // 状态标签
            builder.OpenElement(17, "span");
            builder.AddAttribute(18, "style",
                "margin-left: 6px; padding: 2px 6px; border-radius: var(--radius-sm); " +
                $"background: {(isStreaming ? "var(--color-primary-light, #e6f7ff)" : "var(--color-success-bg)")}; " +
                $"color: {(isStreaming ? "var(--color-primary)" : "var(--color-success)")}; font-size: 11px;");
            builder.AddContent(19, isStreaming ? "思考中..." : $"{content.Length} 字符");
            builder.CloseElement();

            // 流式动画
            if (isStreaming)
            {
                builder.OpenElement(20, "span");
                builder.AddAttribute(21, "style", "margin-left: 8px;");
                builder.AddContent(22, "⏳");
                builder.CloseElement();
            }

            builder.CloseElement(); // reasoning-header

            // 内容区域（流式时展开，完成后折叠）
            if (isStreaming)
            {
                builder.OpenElement(23, "div");
                builder.AddAttribute(24, "class", "reasoning-content");
                builder.AddAttribute(25, "style",
                    "padding: 8px 12px; background: var(--color-bg-tertiary); " +
                    "border-radius: var(--radius-sm); margin-top: 2px; " +
                    "border-left: 3px solid var(--color-primary);");

                builder.OpenElement(26, "div");
                builder.AddAttribute(27, "style",
                    "font-size: 12px; color: var(--color-text-secondary); " +
                    "max-height: 300px; overflow-y: auto;");

                // 渲染 Markdown
                var html = context.Cache?.GetOrCreateMarkdown(content, cacheKey) ?? content;
                builder.AddContent(28, new MarkupString(html));

                builder.CloseElement(); // inner div
                builder.CloseElement(); // reasoning-content
            }

            builder.CloseElement(); // content-block-reasoning
        };
    }

    /// <inheritdoc/>
    public bool CanRender(ContentBlock block)
    {
        return block.Type == ContentBlockType.Reasoning;
    }
}
