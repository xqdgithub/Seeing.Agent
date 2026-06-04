using Microsoft.AspNetCore.Components;
using Seeing.Agent.WebUI.Models.Messaging;

namespace Seeing.Agent.WebUI.Rendering.Abstractions;

/// <summary>
/// 内容块渲染器基接口 - 非泛型版本，用于注册和查找
/// </summary>
/// <remarks>
/// <para>此接口是所有内容块渲染器的基础接口，用于渲染器注册表的统一管理。</para>
/// <para>
/// ⚠️ <strong>线程安全要求：</strong>
/// <list type="bullet">
///   <item><description>渲染器实现必须是无状态的（不允许有实例字段存储渲染状态）</description></item>
///   <item><description>所有渲染状态必须通过 <see cref="RenderContext"/> 传递</description></item>
///   <item><description>在 <see cref="Render"/> 返回的 <see cref="RenderFragment"/> 闭包中，不允许捕获可变状态</description></item>
///   <item><description>只读的静态常量和配置属性是允许的</description></item>
/// </list>
/// </para>
/// </remarks>
public interface IContentBlockRenderer
{
    /// <summary>
    /// 渲染器支持的块类型
    /// </summary>
    ContentBlockType BlockType { get; }

    /// <summary>
    /// 渲染优先级（数值越小优先级越高，越先渲染）
    /// </summary>
    /// <remarks>
    /// 建议的优先级范围：
    /// <list type="bullet">
    ///   <item><description>1-20: 推理/思考过程等前置内容</description></item>
    ///   <item><description>21-50: 工具调用等重要交互元素</description></item>
    ///   <item><description>51-100: 附件/图片等媒体内容</description></item>
    ///   <item><description>101-200: 文本/错误等常规内容</description></item>
    ///   <item><description>201+: 分隔线/装饰等辅助元素</description></item>
    /// </list>
    /// </remarks>
    int Priority { get; }

    /// <summary>
    /// 渲染器名称（用于日志、调试和错误报告）
    /// </summary>
    string Name { get; }

    /// <summary>
    /// 渲染内容块
    /// </summary>
    /// <param name="block">要渲染的内容块</param>
    /// <param name="context">渲染上下文，包含所有渲染所需信息</param>
    /// <returns>渲染片段，可直接添加到 Blazor 组件树</returns>
    /// <remarks>
    /// ⚠️ 实现必须遵循线程安全要求，参见接口文档说明。
    /// </remarks>
    RenderFragment Render(ContentBlock block, RenderContext context);

    /// <summary>
    /// 判断是否可以渲染该内容块
    /// </summary>
    /// <param name="block">要检查的内容块</param>
    /// <returns>如果可以渲染返回 true，否则返回 false</returns>
    /// <remarks>
    /// 此方法用于在渲染前进行前置条件检查，例如：
    /// <list type="bullet">
    ///   <item><description>检查必要的数据是否存在</description></item>
    ///   <item><description>检查内容块状态是否满足渲染条件</description></item>
    /// </list>
    /// </remarks>
    bool CanRender(ContentBlock block);
}

/// <summary>
/// 内容块渲染器泛型接口 - 提供类型安全的渲染扩展
/// </summary>
/// <typeparam name="TBlock">内容块类型，必须是 <see cref="ContentBlock"/> 的子类</typeparam>
/// <remarks>
/// <para>此接口继承自 <see cref="IContentBlockRenderer"/>，提供类型安全的渲染方法。</para>
/// <para>
/// 实现指南：
/// <list type="number">
///   <item><description>实现 <see cref="RenderTyped"/> 方法进行实际渲染</description></item>
///   <item><description>基接口的 Render 方法会自动调用 RenderTyped</description></item>
///   <item><description>如果类型不匹配，会抛出 <see cref="ArgumentException"/></description></item>
/// </list>
/// </para>
/// </remarks>
public interface IContentBlockRenderer<TBlock> : IContentBlockRenderer
    where TBlock : ContentBlock
{
    /// <summary>
    /// 渲染特定类型的内容块（类型安全版本）
    /// </summary>
    /// <param name="block">要渲染的内容块，类型已确保为 <typeparamref name="TBlock"/></param>
    /// <param name="context">渲染上下文</param>
    /// <returns>渲染片段</returns>
    RenderFragment RenderTyped(TBlock block, RenderContext context);
}
