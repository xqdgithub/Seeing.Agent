using Microsoft.AspNetCore.Components;
using Seeing.Agent.WebUI.Models.Messaging;
using Seeing.Agent.WebUI.Rendering.Abstractions;

namespace Seeing.Agent.WebUI.Rendering;

/// <summary>
/// 内容块渲染器注册表实现
/// </summary>
/// <remarks>
/// <para>
/// 此类负责管理所有内容块渲染器的注册、查找和调用。
/// 渲染器通过依赖注入自动注册，按优先级排序。
/// </para>
/// <para>
/// ⚠️ <strong>线程安全：</strong>
/// 此类是线程安全的。渲染器注册在构造函数中完成，之后只读访问。
/// TryRender 方法可以安全地并发调用。
/// </para>
/// </remarks>
public class ContentBlockRendererRegistry : IContentBlockRendererRegistry
{
    private readonly Dictionary<ContentBlockType, IContentBlockRenderer> _renderers;
    private readonly ILogger<ContentBlockRendererRegistry> _logger;

    /// <summary>
    /// 创建渲染器注册表
    /// </summary>
    /// <param name="renderers">所有注入的渲染器</param>
    /// <param name="logger">日志器</param>
    /// <remarks>
    /// 渲染器按优先级自动排序并注册。如果同一类型有多个渲染器，
    /// 只保留优先级最高（数值最小）的那个。
    /// </remarks>
    public ContentBlockRendererRegistry(
        IEnumerable<IContentBlockRenderer> renderers,
        ILogger<ContentBlockRendererRegistry> logger)
    {
        _logger = logger;
        _renderers = new Dictionary<ContentBlockType, IContentBlockRenderer>();

        // 按优先级排序后注册
        foreach (var renderer in renderers.OrderBy(r => r.Priority))
        {
            Register(renderer);
        }

        _logger.LogInformation(
            "Content block renderer registry initialized with {Count} renderers: {Renderers}",
            _renderers.Count,
            string.Join(", ", _renderers.Values.Select(r => $"{r.Name}({r.BlockType}:{r.Priority})")));
    }

    /// <summary>
    /// 已注册的渲染器数量
    /// </summary>
    public int Count => _renderers.Count;

    /// <summary>
    /// 注册渲染器
    /// </summary>
    /// <param name="renderer">要注册的渲染器</param>
    /// <remarks>
    /// 如果同一类型已有渲染器，会记录警告并替换为新渲染器。
    /// </remarks>
    public void Register(IContentBlockRenderer renderer)
    {
        var blockType = renderer.BlockType;

        if (_renderers.TryGetValue(blockType, out var existing))
        {
            _logger.LogWarning(
                "Renderer for {BlockType} already registered with {ExistingRenderer} (priority {ExistingPriority}), " +
                "replacing with {NewRenderer} (priority {NewPriority})",
                blockType, existing.Name, existing.Priority, renderer.Name, renderer.Priority);
        }

        _renderers[blockType] = renderer;
        _logger.LogDebug(
            "Registered renderer {RendererName} for {BlockType} (Priority: {Priority})",
            renderer.Name, blockType, renderer.Priority);
    }

    /// <summary>
    /// 获取指定类型的渲染器
    /// </summary>
    /// <param name="type">内容块类型</param>
    /// <returns>渲染器，如果未找到返回 null</returns>
    public IContentBlockRenderer? GetRenderer(ContentBlockType type)
    {
        return _renderers.TryGetValue(type, out var renderer) ? renderer : null;
    }

    /// <summary>
    /// 获取所有已注册的渲染器
    /// </summary>
    /// <returns>按优先级排序的渲染器集合</returns>
    public IEnumerable<IContentBlockRenderer> GetAllRenderers()
    {
        return _renderers.Values.OrderBy(r => r.Priority);
    }

    /// <summary>
    /// 尝试渲染内容块
    /// </summary>
    /// <param name="block">要渲染的内容块</param>
    /// <param name="context">渲染上下文</param>
    /// <param name="fragment">输出的渲染片段</param>
    /// <returns>总是返回 true，因为有降级渲染保证</returns>
    /// <remarks>
    /// <para>
    /// 渲染流程：
    /// <list type="number">
    ///   <item><description>查找对应类型的渲染器</description></item>
    ///   <item><description>检查渲染器是否能处理该块</description></item>
    ///   <item><description>调用渲染器进行渲染</description></item>
    ///   <item><description>如果出错，使用错误降级渲染</description></item>
    ///   <item><description>如果未找到渲染器，使用默认降级渲染</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    public bool TryRender(ContentBlock block, RenderContext context, out RenderFragment? fragment)
    {
        if (block == null)
        {
            _logger.LogWarning("Attempted to render null block, using empty fragment");
            fragment = EmptyFragment;
            return true;
        }

        fragment = RenderWithFallback(block, context);
        return true;
    }

    /// <summary>
    /// 带降级处理的渲染
    /// </summary>
    private RenderFragment RenderWithFallback(ContentBlock block, RenderContext context)
    {
        return builder =>
        {
            try
            {
                if (_renderers.TryGetValue(block.Type, out var renderer))
                {
                    if (renderer.CanRender(block))
                    {
                        var result = renderer.Render(block, context);
                        builder.AddContent(0, result);
                    }
                    else
                    {
                        _logger.LogDebug(
                            "Renderer {RendererName} cannot render block {BlockId}, using fallback",
                            renderer.Name, block.Id);
                        builder.AddContent(0, RenderFallback(block));
                    }
                }
                else
                {
                    _logger.LogDebug(
                        "No renderer found for block type {BlockType}, using fallback",
                        block.Type);
                    builder.AddContent(0, RenderFallback(block));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error rendering block {BlockType}:{BlockId} with renderer, using error fallback",
                    block.Type, block.Id);
                builder.AddContent(0, RenderErrorFallback(block, ex));
            }
        };
    }

    /// <summary>
    /// 空片段
    /// </summary>
    private static readonly RenderFragment EmptyFragment = _ => { };

    /// <summary>
    /// 默认降级渲染（未找到渲染器时使用）
    /// </summary>
    private static RenderFragment RenderFallback(ContentBlock block)
    {
        return builder =>
        {
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "class", "content-block-fallback");
            builder.AddAttribute(2, "style",
                "padding: var(--space-2); background: var(--color-bg-tertiary); " +
                "border-radius: var(--radius-sm); color: var(--color-text-secondary); " +
                "font-size: var(--font-size-sm);");

            if (!string.IsNullOrEmpty(block.Content))
            {
                builder.AddContent(3, block.Content);
            }
            else
            {
                builder.AddContent(3, $"[{block.Type}]");
            }

            builder.CloseElement();
        };
    }

    /// <summary>
    /// 错误降级渲染（渲染出错时使用）
    /// </summary>
    private static RenderFragment RenderErrorFallback(ContentBlock block, Exception ex)
    {
        return builder =>
        {
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "class", "content-block-error");
            builder.AddAttribute(2, "style",
                "padding: var(--space-2) var(--space-3); background: var(--color-error-bg); " +
                "border: 1px solid var(--color-error-border); border-radius: var(--radius-sm); " +
                "margin: 4px 0;");

            // 错误图标
            builder.OpenElement(3, "span");
            builder.AddAttribute(4, "style", "color: var(--color-error); margin-right: var(--space-1);");
            builder.AddContent(5, "⚠️");
            builder.CloseElement();

            // 错误信息
            builder.OpenElement(6, "span");
            builder.AddAttribute(7, "style", "color: var(--color-error);");
            builder.AddContent(8, $"渲染错误: {ex.Message}");
            builder.CloseElement();

            // 原始内容（如果有）
            if (!string.IsNullOrEmpty(block.Content))
            {
                builder.OpenElement(9, "div");
                builder.AddAttribute(10, "style",
                    "margin-top: var(--space-2); padding: var(--space-2); " +
                    "background: var(--color-bg-tertiary); border-radius: var(--radius-sm); " +
                    "font-size: var(--font-size-sm); max-height: 100px; overflow: auto;");
                builder.AddContent(11, block.Content);
                builder.CloseElement();
            }

            builder.CloseElement();
        };
    }
}
