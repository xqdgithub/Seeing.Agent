using Microsoft.AspNetCore.Components;
using Seeing.Agent.WebUI.Models;

namespace Seeing.Agent.WebUI.Rendering.Abstractions;

/// <summary>
/// 消息渲染管线接口
/// </summary>
public interface IMessageRenderPipeline
{
    /// <summary>
    /// 渲染单条消息
    /// </summary>
    /// <param name="message">消息数据</param>
    /// <param name="options">渲染选项</param>
    /// <param name="serviceProvider">服务提供者（可选）</param>
    /// <param name="onToolClick">工具点击回调（可选）</param>
    RenderFragment RenderMessage(
        MessageViewModel message,
        RenderOptions? options = null,
        IServiceProvider? serviceProvider = null,
        EventCallback<ToolCallViewModel> onToolClick = default);

    /// <summary>
    /// 渲染 Loop 分组
    /// </summary>
    /// <param name="loop">Loop 分组数据</param>
    /// <param name="options">渲染选项</param>
    /// <param name="serviceProvider">服务提供者（可选）</param>
    /// <param name="onToolClick">工具点击回调（可选）</param>
    RenderFragment RenderLoop(
        LoopGroupViewModel loop,
        RenderOptions? options = null,
        IServiceProvider? serviceProvider = null,
        EventCallback<ToolCallViewModel> onToolClick = default);

    /// <summary>
    /// 渲染消息列表
    /// </summary>
    RenderFragment RenderMessageList(
        IReadOnlyList<MessageViewModel> messages,
        MessageListOptions? options = null);

    /// <summary>
    /// 构建渲染上下文
    /// </summary>
    /// <param name="message">消息视图模型</param>
    /// <param name="options">渲染选项</param>
    /// <param name="serviceProvider">服务提供者（可选）</param>
    /// <param name="onToolClick">工具点击回调（可选）</param>
    /// <returns>渲染上下文</returns>
    RenderContext BuildContext(
        MessageViewModel? message,
        RenderOptions? options = null,
        IServiceProvider? serviceProvider = null,
        EventCallback<ToolCallViewModel> onToolClick = default);
}

/// <summary>
/// 消息列表渲染选项
/// </summary>
public class MessageListOptions
{
    /// <summary>
    /// 消息渲染选项
    /// </summary>
    public RenderOptions RenderOptions { get; set; } = new();

    /// <summary>
    /// 是否自动滚动
    /// </summary>
    public bool AutoScroll { get; set; } = true;

    /// <summary>
    /// 是否显示时间戳
    /// </summary>
    public bool ShowTimestamp { get; set; } = true;

    /// <summary>
    /// 消息间距
    /// </summary>
    public string MessageGap { get; set; } = "var(--space-4)";

    /// <summary>
    /// 最大消息宽度
    /// </summary>
    public string MaxMessageWidth { get; set; } = "var(--message-max-width)";

    /// <summary>
    /// 工具点击回调
    /// </summary>
    public EventCallback<ToolCallViewModel>? OnToolClick { get; set; }
}
