using Microsoft.Extensions.DependencyInjection.Extensions;
using Seeing.Agent.WebUI.Models.Messaging;
using Seeing.Agent.WebUI.Rendering.Abstractions;
using Seeing.Agent.WebUI.Rendering.Caching;
using Seeing.Agent.WebUI.Rendering.Components;
using MessagingComponents = Seeing.Agent.WebUI.Components.Messaging;

namespace Seeing.Agent.WebUI.Rendering;

/// <summary>
/// 消息渲染服务注册扩展
/// </summary>
/// <remarks>
/// <para>
/// 使用方法：
/// <code>
/// services.AddMessageRendering();
/// </code>
/// </para>
/// <para>
/// 此扩展会注册以下服务：
/// <list type="bullet">
///   <item><description><see cref="IRenderCache"/> - Markdown 渲染缓存</description></item>
///   <item><description><see cref="IContentBlockRendererRegistry"/> - 渲染器注册表</description></item>
///   <item><description><see cref="IMessageComponentRegistry"/> - 消息组件注册表</description></item>
///   <item><description><see cref="IMessageRenderPipeline"/> - 消息渲染管线</description></item>
///   <item><description>所有内置渲染器</description></item>
///   <item><description>所有内置消息组件</description></item>
/// </list>
/// </para>
/// </remarks>
public static class RenderingServiceExtensions
{
    /// <summary>
    /// 添加消息渲染服务
    /// </summary>
    /// <param name="services">服务集合</param>
    /// <returns>服务集合（支持链式调用）</returns>
    /// <remarks>
    /// <para>
    /// 服务生命周期说明：
    /// <list type="bullet">
    ///   <item><description>Singleton: 缓存、渲染器注册表、组件注册表、渲染器、组件</description></item>
    ///   <item><description>Scoped: 渲染管线</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// ⚠️ 渲染器和组件是单例，必须是无状态的。参见 <see cref="IContentBlockRenderer"/> 和 <see cref="IMessageComponent"/> 文档。
    /// </para>
    /// </remarks>
    public static IServiceCollection AddMessageRendering(this IServiceCollection services)
    {
        // ========== 核心服务 ==========

        // 缓存服务（单例，全局共享）
        services.TryAddSingleton<IRenderCache, MemoryRenderCache>();

        // 渲染器注册表（单例，启动时初始化）
        services.TryAddSingleton<IContentBlockRendererRegistry, ContentBlockRendererRegistry>();

        // 消息组件注册表（单例，启动时初始化）
        services.TryAddSingleton<IMessageComponentRegistry, MessageComponentRegistry>();

        // 渲染管线（Scoped，每个请求一个实例）
        services.TryAddScoped<IMessageRenderPipeline, MessageRenderPipeline>();

        // ========== 内置渲染器 ==========
        // 按优先级顺序注册（数值越小优先级越高）
        // 注意：这些渲染器作为组件的回退使用

        // 优先级 1-20: 推理/思考过程等前置内容
        services.AddSingleton<IContentBlockRenderer, Renderers.ReasoningBlockRenderer>();

        // 优先级 51-100: 附件/图片等媒体内容
        services.AddSingleton<IContentBlockRenderer, Renderers.ImageBlockRenderer>();
        services.AddSingleton<IContentBlockRenderer, Renderers.AttachmentBlockRenderer>();

        // 优先级 101-200: 文本/错误等常规内容
        services.AddSingleton<IContentBlockRenderer, Renderers.TextBlockRenderer>();
        services.AddSingleton<IContentBlockRenderer, Renderers.ErrorBlockRenderer>();
        services.AddSingleton<IContentBlockRenderer, Renderers.SubAgentBlockRenderer>();
        services.AddSingleton<IContentBlockRenderer, Renderers.PermissionBlockRenderer>();

        // 优先级 201+: 分隔线/装饰等辅助元素
        services.AddSingleton<IContentBlockRenderer, Renderers.DividerBlockRenderer>();

        // ========== 内置消息组件 ==========
        // 按优先级顺序注册（数值越小优先级越高）

        // 优先级 10: 推理消息组件
        services.AddSingleton<IMessageComponent>(sp =>
            new DefaultMessageComponent<MessagingComponents.ReasoningMessageComponent>(
                ContentBlockType.Reasoning,
                10,
                "Reasoning",
                block => block.Type == ContentBlockType.Reasoning,
                (block, context) => new Dictionary<string, object?>
                {
                    ["Content"] = block.Content ?? string.Empty,
                    ["Block"] = block,
                    ["Context"] = context
                }));

        // 优先级 50: 工具调用消息组件
        services.AddSingleton<IMessageComponent>(sp =>
            new DefaultMessageComponent<MessagingComponents.ToolCallMessageComponent>(
                ContentBlockType.ToolCall,
                50,
                "ToolCall",
                block => block.Type == ContentBlockType.ToolCall && block.ToolCall != null,
                (block, context) => new Dictionary<string, object?>
                {
                    ["ToolCall"] = block.ToolCall!,
                    ["Block"] = block,
                    ["Context"] = context,
                    ["OnToolClick"] = context.OnToolClick
                }));

        // 优先级 100: 文本消息组件
        services.AddSingleton<IMessageComponent>(sp =>
            new DefaultMessageComponent<MessagingComponents.TextMessageComponent>(
                ContentBlockType.Text,
                100,
                "Text",
                block => block.Type == ContentBlockType.Text && block.Content != null,
                (block, context) => new Dictionary<string, object?>
                {
                    ["Content"] = block.Content ?? string.Empty,
                    ["Block"] = block,
                    ["Context"] = context
                }));

        // 优先级 110: 错误消息组件
        services.AddSingleton<IMessageComponent>(sp =>
            new DefaultMessageComponent<MessagingComponents.ErrorMessageComponent>(
                ContentBlockType.Error,
                110,
                "Error",
                block => block.Type == ContentBlockType.Error,
                (block, context) => new Dictionary<string, object?>
                {
                    ["Content"] = block.Content ?? string.Empty,
                    ["Block"] = block,
                    ["Context"] = context
                }));

        // 优先级 120: 附件消息组件
        services.AddSingleton<IMessageComponent>(sp =>
            new DefaultMessageComponent<MessagingComponents.AttachmentMessageComponent>(
                ContentBlockType.Attachment,
                120,
                "Attachment",
                block => block.Type == ContentBlockType.Attachment && block.Attachment != null,
                (block, context) => new Dictionary<string, object?>
                {
                    ["Attachment"] = block.Attachment!,
                    ["Block"] = block,
                    ["Context"] = context
                }));

        // 优先级 130: 权限消息组件
        services.AddSingleton<IMessageComponent>(sp =>
            new DefaultMessageComponent<MessagingComponents.PermissionMessageComponent>(
                ContentBlockType.Permission,
                130,
                "Permission",
                block => block.Type == ContentBlockType.Permission && block.Extensions?.ContainsKey("permission") == true,
                (block, context) => new Dictionary<string, object?>
                {
                    ["Permission"] = block.Extensions!["permission"],
                    ["Block"] = block,
                    ["Context"] = context
                }));

        // 优先级 140: 分隔线消息组件
        services.AddSingleton<IMessageComponent>(sp =>
            new DefaultMessageComponent<MessagingComponents.DividerMessageComponent>(
                ContentBlockType.Divider,
                140,
                "Divider",
                block => block.Type == ContentBlockType.Divider,
                (block, context) => new Dictionary<string, object?>
                {
                    ["StepIndex"] = block.Extensions?.TryGetValue("stepIndex", out var step) == true ? (int)step : 0,
                    ["Block"] = block,
                    ["Context"] = context
                }));

        // 优先级 150: 子代理消息组件
        services.AddSingleton<IMessageComponent>(sp =>
            new DefaultMessageComponent<MessagingComponents.SubAgentMessageComponent>(
                ContentBlockType.SubAgent,
                150,
                "SubAgent",
                block => block.Type == ContentBlockType.SubAgent && block.Extensions?.ContainsKey("subAgentName") == true,
                (block, context) => new Dictionary<string, object?>
                {
                    ["AgentName"] = block.Extensions!["subAgentName"]?.ToString() ?? string.Empty,
                    ["Content"] = block.Content ?? string.Empty,
                    ["IsStreaming"] = block.IsStreaming,
                    ["Block"] = block,
                    ["Context"] = context
                }));

        return services;
    }

    /// <summary>
    /// 添加消息渲染服务（带自定义配置）
    /// </summary>
    /// <param name="services">服务集合</param>
    /// <param name="configure">配置委托</param>
    /// <returns>服务集合</returns>
    public static IServiceCollection AddMessageRendering(
        this IServiceCollection services,
        Action<RenderingOptions> configure)
    {
        var options = new RenderingOptions();
        configure?.Invoke(options);

        // 注册基础服务
        services.AddMessageRendering();

        // 注册自定义渲染器
        foreach (var rendererType in options.CustomRenderers)
        {
            services.AddSingleton(typeof(IContentBlockRenderer), rendererType);
        }

        // 注册自定义消息组件
        foreach (var componentType in options.CustomComponents)
        {
            services.AddSingleton(typeof(IMessageComponent), componentType);
        }

        return services;
    }
}

/// <summary>
/// 渲染配置选项
/// </summary>
public class RenderingOptions
{
    /// <summary>
    /// 自定义渲染器类型列表
    /// </summary>
    public List<Type> CustomRenderers { get; } = new();

    /// <summary>
    /// 自定义消息组件类型列表
    /// </summary>
    public List<Type> CustomComponents { get; } = new();

    /// <summary>
    /// 添加自定义渲染器
    /// </summary>
    /// <typeparam name="TRenderer">渲染器类型</typeparam>
    public void AddRenderer<TRenderer>() where TRenderer : class, IContentBlockRenderer
    {
        CustomRenderers.Add(typeof(TRenderer));
    }

    /// <summary>
    /// 添加自定义消息组件
    /// </summary>
    /// <typeparam name="TComponent">组件类型</typeparam>
    public void AddComponent<TComponent>() where TComponent : class, IMessageComponent
    {
        CustomComponents.Add(typeof(TComponent));
    }
}
