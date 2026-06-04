using Microsoft.AspNetCore.Components;
using Seeing.Agent.WebUI.Models.Messaging;

namespace Seeing.Agent.WebUI.Rendering.Abstractions;

/// <summary>
/// 内容块渲染器注册表接口
/// </summary>
/// <remarks>
/// 此接口定义了渲染器注册表的核心功能：
/// <list type="bullet">
///   <item><description>渲染器注册和管理</description></item>
///   <item><description>渲染器查找</description></item>
///   <item><description>统一的渲染调用入口</description></item>
/// </list>
/// </remarks>
public interface IContentBlockRendererRegistry
{
    /// <summary>
    /// 注册渲染器
    /// </summary>
    /// <param name="renderer">要注册的渲染器</param>
    void Register(IContentBlockRenderer renderer);

    /// <summary>
    /// 获取指定类型的渲染器
    /// </summary>
    /// <param name="type">内容块类型</param>
    /// <returns>渲染器实例，如果未找到返回 null</returns>
    IContentBlockRenderer? GetRenderer(ContentBlockType type);

    /// <summary>
    /// 获取所有已注册的渲染器
    /// </summary>
    /// <returns>按优先级排序的渲染器集合</returns>
    IEnumerable<IContentBlockRenderer> GetAllRenderers();

    /// <summary>
    /// 尝试渲染内容块
    /// </summary>
    /// <param name="block">要渲染的内容块</param>
    /// <param name="context">渲染上下文</param>
    /// <param name="fragment">输出的渲染片段</param>
    /// <returns>
    /// 总是返回 true，因为有降级渲染保证。
    /// 即使未找到渲染器或渲染出错，也会输出有效的渲染片段。
    /// </returns>
    bool TryRender(ContentBlock block, RenderContext context, out RenderFragment? fragment);

    /// <summary>
    /// 已注册的渲染器数量
    /// </summary>
    int Count { get; }
}
