using Microsoft.AspNetCore.Components;
using Seeing.Agent.WebUI.Models.Messaging;
using Seeing.Agent.WebUI.Rendering.Abstractions;

namespace Seeing.Agent.WebUI.Rendering.Renderers;

/// <summary>
/// 文本内容块渲染器
/// </summary>
/// <remarks>
/// ⚠️ <strong>线程安全要求：</strong>
/// <list type="bullet">
///   <item><description>此类是无状态的，所有状态通过 RenderContext 传递</description></item>
///   <item><description>不允许添加实例字段</description></item>
///   <item><description>Render 返回的闭包只能捕获不可变数据</description></item>
/// </list>
/// </remarks>
public class TextBlockRenderer : IContentBlockRenderer
{
    /// <inheritdoc/>
    public ContentBlockType BlockType => ContentBlockType.Text;

    /// <inheritdoc/>
    public int Priority => 100;

    /// <inheritdoc/>
    public string Name => "Text";

    /// <inheritdoc/>
    public RenderFragment Render(ContentBlock block, RenderContext context)
    {
        // ✅ 捕获不可变数据
        var content = block.Content ?? string.Empty;
        var cacheKey = context.GetMarkdownCacheKey(block);

        return builder =>
        {
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "class", "content-block-text message-content");

            // 使用缓存渲染 Markdown
            var html = context.Cache?.GetOrCreateMarkdown(content, cacheKey) ?? content;

            builder.AddContent(2, new MarkupString(html));
            builder.CloseElement();
        };
    }

    /// <inheritdoc/>
    public bool CanRender(ContentBlock block)
    {
        return block.Type == ContentBlockType.Text && block.Content != null;
    }
}
