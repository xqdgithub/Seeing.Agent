using Microsoft.AspNetCore.Components;
using Seeing.Agent.WebUI.Models.Messaging;

namespace Seeing.Agent.WebUI.Rendering.Abstractions;

/// <summary>
/// 消息组件接口 - 定义消息渲染的通用方法
/// </summary>
/// <remarks>
/// <para>
/// 所有消息类型组件都应实现此接口，提供统一的渲染方式：
/// <list type="bullet">
///   <item><description>支持参数传递</description></item>
///   <item><description>支持事件回调</description></item>
///   <item><description>支持状态管理</description></item>
/// </list>
/// </para>
/// </remarks>
public interface IMessageComponent
{
    /// <summary>
    /// 消息类型
    /// </summary>
    ContentBlockType BlockType { get; }

    /// <summary>
    /// 组件优先级（数字越小优先级越高）
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// 组件名称
    /// </summary>
    string Name { get; }

    /// <summary>
    /// 是否可以渲染指定的内容块
    /// </summary>
    /// <param name="block">内容块</param>
    /// <returns>是否可以渲染</returns>
    bool CanRender(ContentBlock block);

    /// <summary>
    /// 获取组件类型
    /// </summary>
    /// <returns>组件类型</returns>
    Type GetComponentType();

    /// <summary>
    /// 获取组件参数
    /// </summary>
    /// <param name="block">内容块</param>
    /// <param name="context">渲染上下文</param>
    /// <returns>组件参数字典</returns>
    Dictionary<string, object?> GetComponentParameters(ContentBlock block, RenderContext context);
}

/// <summary>
/// 泛型消息组件接口
/// </summary>
/// <typeparam name="TComponent">组件类型</typeparam>
public interface IMessageComponent<TComponent> : IMessageComponent where TComponent : IComponent
{
    /// <summary>
    /// 获取组件类型
    /// </summary>
    /// <returns>组件类型</returns>
    new Type GetComponentType() => typeof(TComponent);
}

/// <summary>
/// 消息组件基类 - 提供默认实现
/// </summary>
/// <typeparam name="TComponent">组件类型</typeparam>
public abstract class MessageComponentBase<TComponent> : IMessageComponent<TComponent> where TComponent : IComponent
{
    /// <inheritdoc/>
    public abstract ContentBlockType BlockType { get; }

    /// <inheritdoc/>
    public virtual int Priority => 100;

    /// <inheritdoc/>
    public virtual string Name => typeof(TComponent).Name;

    /// <inheritdoc/>
    public abstract bool CanRender(ContentBlock block);

    /// <inheritdoc/>
    public virtual Type GetComponentType() => typeof(TComponent);

    /// <inheritdoc/>
    public abstract Dictionary<string, object?> GetComponentParameters(ContentBlock block, RenderContext context);
}
