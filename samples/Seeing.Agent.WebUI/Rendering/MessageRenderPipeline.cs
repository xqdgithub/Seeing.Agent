using Microsoft.AspNetCore.Components;
using Seeing.Agent.WebUI.Components.Messaging;
using Seeing.Agent.WebUI.Models;
using Seeing.Agent.WebUI.Models.Messaging;
using Seeing.Agent.WebUI.Rendering.Abstractions;
using Seeing.Agent.WebUI.Rendering.Components;
using Seeing.Agent.WebUI.State;

namespace Seeing.Agent.WebUI.Rendering;

/// <summary>
/// 消息渲染管线实现
/// </summary>
/// <remarks>
/// <para>
/// 此类是消息渲染的核心入口，负责：
/// <list type="bullet">
///   <item><description>协调消息头部渲染</description></item>
///   <item><description>调用组件注册表渲染内容块</description></item>
///   <item><description>处理 Loop 分组渲染</description></item>
///   <item><description>管理消息列表渲染</description></item>
/// </list>
/// </para>
/// <para>
/// ⚠️ <strong>线程安全：</strong>
/// 此类不是线程安全的。应该在每个 Blazor 组件的渲染上下文中使用。
/// </para>
/// </remarks>
public class MessageRenderPipeline : IMessageRenderPipeline
{
    private readonly IContentBlockRendererRegistry _registry;
    private readonly IMessageComponentRegistry _componentRegistry;
    private readonly IRenderCache _cache;
    private readonly SessionState _sessionState;
    private readonly ILogger<MessageRenderPipeline> _logger;

    /// <summary>
    /// 创建消息渲染管线
    /// </summary>
    /// <param name="registry">内容块渲染器注册表</param>
    /// <param name="componentRegistry">消息组件注册表</param>
    /// <param name="cache">渲染缓存</param>
    /// <param name="sessionState">会话状态（用于获取 SessionId）</param>
    /// <param name="logger">日志器</param>
    public MessageRenderPipeline(
        IContentBlockRendererRegistry registry,
        IMessageComponentRegistry componentRegistry,
        IRenderCache cache,
        SessionState sessionState,
        ILogger<MessageRenderPipeline> logger)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _componentRegistry = componentRegistry ?? throw new ArgumentNullException(nameof(componentRegistry));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _sessionState = sessionState ?? throw new ArgumentNullException(nameof(sessionState));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// 渲染单条消息
    /// </summary>
    /// <param name="message">消息视图模型</param>
    /// <param name="options">渲染选项（可选）</param>
    /// <param name="serviceProvider">服务提供者（可选）</param>
    /// <param name="onToolClick">工具点击回调（可选）</param>
    /// <returns>渲染片段</returns>
    public RenderFragment RenderMessage(
        MessageViewModel message,
        RenderOptions? options = null,
        IServiceProvider? serviceProvider = null,
        EventCallback<ToolCallViewModel> onToolClick = default)
    {
        if (message == null)
        {
            return builder => { };
        }

        var context = BuildContext(message, options, serviceProvider, onToolClick);
        var blocks = ContentBlockBuilder.BuildFromMessage(message);

        return builder =>
        {
            var seq = 0;

            // 渲染消息容器
            builder.OpenElement(seq++, "div");
            builder.AddAttribute(seq++, "class", GetMessageClass(message));

            // 渲染消息头部
            builder.OpenComponent<MessageHeader>(seq++);
            builder.AddAttribute(seq++, "Role", message.Role);
            builder.AddAttribute(seq++, "Timestamp", message.Timestamp);
            builder.AddAttribute(seq++, "Status", DetermineStatus(message));
            builder.AddAttribute(seq++, "ShowStatusIndicator", !message.IsComplete);
            builder.CloseComponent();

            // 渲染内容块（优先使用组件，回退到渲染器）
            foreach (var block in blocks.OrderBy(b => b.SortIndex))
            {
                if (_componentRegistry.TryGetComponent(block, out var component))
                {
                    // 使用组件渲染
                    builder.OpenComponent(seq++, component.GetComponentType());
                    var parameters = component.GetComponentParameters(block, context);
                    foreach (var param in parameters)
                    {
                        builder.AddAttribute(seq++, param.Key, param.Value);
                    }
                    builder.CloseComponent();
                }
                else if (_registry.TryRender(block, context, out var fragment))
                {
                    // 回退到渲染器
                    builder.AddContent(seq++, fragment);
                }
            }

            builder.CloseElement();
        };
    }

    /// <summary>
    /// 渲染 Loop 分组
    /// </summary>
    /// <param name="loop">Loop 分组视图模型</param>
    /// <param name="options">渲染选项（可选）</param>
    /// <param name="serviceProvider">服务提供者（可选）</param>
    /// <param name="onToolClick">工具点击回调（可选）</param>
    /// <returns>渲染片段</returns>
    public RenderFragment RenderLoop(
        LoopGroupViewModel loop,
        RenderOptions? options = null,
        IServiceProvider? serviceProvider = null,
        EventCallback<ToolCallViewModel> onToolClick = default)
    {
        if (loop == null || loop.Messages.Count == 0)
        {
            return builder => { };
        }

        var firstMessage = loop.Messages.First();
        var context = BuildContext(firstMessage, options, serviceProvider, onToolClick);
        var blocks = ContentBlockBuilder.BuildFromLoopMessages(loop.Messages.ToList());

        return builder =>
        {
            var seq = 0;

            // 渲染消息容器
            builder.OpenElement(seq++, "div");
            builder.AddAttribute(seq++, "class", GetLoopClass(loop));

            // 渲染 Loop 头部
            builder.OpenComponent<MessageHeader>(seq++);
            builder.AddAttribute(seq++, "Role", "assistant");
            builder.AddAttribute(seq++, "Timestamp", loop.StartTime ?? DateTime.Now);
            builder.AddAttribute(seq++, "Status", DetermineLoopStatus(loop));
            builder.AddAttribute(seq++, "ShowStatusIndicator", loop.IsExecuting);
            builder.AddAttribute(seq++, "Tags", BuildLoopTags(loop));
            builder.AddAttribute(seq++, "MetaItems", BuildLoopMetaItems(loop));
            builder.AddAttribute(seq++, "CustomRoleName", "助手");
            builder.CloseComponent();

            // 渲染内容块（优先使用组件，回退到渲染器）
            foreach (var block in blocks.OrderBy(b => b.SortIndex))
            {
                if (_componentRegistry.TryGetComponent(block, out var component))
                {
                    // 使用组件渲染
                    builder.OpenComponent(seq++, component.GetComponentType());
                    var parameters = component.GetComponentParameters(block, context);
                    foreach (var param in parameters)
                    {
                        builder.AddAttribute(seq++, param.Key, param.Value);
                    }
                    builder.CloseComponent();
                }
                else if (_registry.TryRender(block, context, out var fragment))
                {
                    // 回退到渲染器
                    builder.AddContent(seq++, fragment);
                }
            }

            // 渲染错误信息
            if (!string.IsNullOrEmpty(loop.Error))
            {
                builder.OpenElement(seq++, "div");
                builder.AddAttribute(seq++, "class", "loop-error");
                builder.AddAttribute(seq++, "style",
                    "padding: var(--space-2) var(--space-3); background: var(--color-error-bg); " +
                    "border: 1px solid var(--color-error-border); border-radius: var(--radius-sm); " +
                    "margin-top: var(--space-2); color: var(--color-error);");
                builder.AddContent(seq++, loop.Error);
                builder.CloseElement();
            }

            builder.CloseElement();
        };
    }

    /// <summary>
    /// 渲染消息列表
    /// </summary>
    /// <param name="messages">消息列表</param>
    /// <param name="options">消息列表选项（可选）</param>
    /// <returns>渲染片段</returns>
    public RenderFragment RenderMessageList(
        IReadOnlyList<MessageViewModel> messages,
        MessageListOptions? options = null)
    {
        if (messages == null || messages.Count == 0)
        {
            return builder =>
            {
                builder.OpenElement(0, "div");
                builder.AddAttribute(1, "class", "message-list-empty");
                builder.AddAttribute(2, "style",
                    "flex: 1; display: flex; align-items: center; justify-content: center; " +
                    "padding: var(--space-16); color: var(--color-text-secondary);");
                builder.AddContent(3, "暂无消息，开始对话吧");
                builder.CloseElement();
            };
        }

        options ??= new MessageListOptions();

        return builder =>
        {
            var seq = 0;

            builder.OpenElement(seq++, "div");
            builder.AddAttribute(seq++, "class", "message-list-container");
            builder.AddAttribute(seq++, "style",
                $"flex: 1; overflow-y: auto; padding: var(--space-4); " +
                $"display: flex; flex-direction: column; gap: {options.MessageGap};");

            // 已处理的 LoopId 集合
            var processedLoopIds = new HashSet<string>();

            foreach (var message in messages)
            {
                if (message.Role == "assistant" && !string.IsNullOrEmpty(message.LoopId))
                {
                    // 检查是否已渲染该 Loop
                    if (processedLoopIds.Contains(message.LoopId))
                    {
                        continue;
                    }
                    processedLoopIds.Add(message.LoopId);

                    // 需要 LoopGroupViewModel，这里简化处理
                    // 实际实现中应该注入 ILoopGroupBuilder
                    builder.AddContent(seq++, RenderMessage(message, options.RenderOptions));
                }
                else
                {
                    builder.AddContent(seq++, RenderMessage(message, options.RenderOptions));
                }
            }

            builder.CloseElement();
        };
    }

    /// <summary>
    /// 构建渲染上下文
    /// </summary>
    /// <param name="message">消息视图模型</param>
    /// <param name="options">渲染选项</param>
    /// <param name="serviceProvider">服务提供者（可选，用于依赖注入）</param>
    /// <param name="onToolClick">工具点击回调（可选）</param>
    /// <returns>渲染上下文</returns>
    public RenderContext BuildContext(
        MessageViewModel? message,
        RenderOptions? options = null,
        IServiceProvider? serviceProvider = null,
        EventCallback<ToolCallViewModel> onToolClick = default)
    {
        return new RenderContext
        {
            SessionId = _sessionState.SessionId,
            MessageId = message?.Id ?? string.Empty,
            LoopId = message?.LoopId,
            IsStreaming = message?.IsComplete == false,
            Options = options ?? new RenderOptions(),
            Cache = _cache,
            ServiceProvider = serviceProvider,
            OnToolClick = onToolClick
        };
    }

    // ========== 辅助方法 ==========

    private static string GetMessageClass(MessageViewModel message)
    {
        var roleClass = message.Role.ToLowerInvariant();
        var statusClass = GetStatusClass(message);
        return $"message-item message-{roleClass} {statusClass}".Trim();
    }

    private static string GetStatusClass(MessageViewModel message)
    {
        if (!message.IsComplete)
            return "message-streaming";
        if (message.ToolCalls.Any(t => !string.IsNullOrEmpty(t.Error)))
            return "message-error";
        return "message-complete";
    }

    private static string GetLoopClass(LoopGroupViewModel loop)
    {
        var statusClass = loop.IsExecuting ? "message-streaming" :
                          loop.Success ? "message-complete" : "message-error";
        return $"message-item message-assistant {statusClass}";
    }

    private static MessageStatus DetermineStatus(MessageViewModel message)
    {
        if (!message.IsComplete)
            return MessageStatus.Streaming;
        if (message.ToolCalls.Any(t => !string.IsNullOrEmpty(t.Error)))
            return MessageStatus.Error;
        return MessageStatus.Complete;
    }

    private static MessageStatus DetermineLoopStatus(LoopGroupViewModel loop)
    {
        if (loop.IsExecuting)
            return MessageStatus.Streaming;
        return loop.Success ? MessageStatus.Complete : MessageStatus.Error;
    }

    private static List<MessageHeader.MessageTag> BuildLoopTags(LoopGroupViewModel loop)
    {
        var tags = new List<MessageHeader.MessageTag>();

        if (loop.LoopIndex > 0)
        {
            tags.Add(new MessageHeader.MessageTag
            {
                Text = $"Loop #{loop.LoopIndex}",
                Color = loop.IsExecuting ? "processing" : loop.Success ? "success" : "error"
            });
        }

        if (loop.TotalSteps > 1)
        {
            tags.Add(new MessageHeader.MessageTag
            {
                Text = $"{loop.TotalSteps} 步骤",
                Color = "default"
            });
        }

        return tags;
    }

    private static List<MessageHeader.MessageMetaItem> BuildLoopMetaItems(LoopGroupViewModel loop)
    {
        var items = new List<MessageHeader.MessageMetaItem>();

        if (loop.TotalToolCalls > 0)
        {
            items.Add(new MessageHeader.MessageMetaItem
            {
                Title = "工具调用次数",
                Value = $"{loop.TotalToolCalls} 工具",
                Icon = "tool"
            });
        }

        if (!string.IsNullOrEmpty(loop.GetFormattedDuration()))
        {
            items.Add(new MessageHeader.MessageMetaItem
            {
                Title = "执行耗时",
                Value = loop.GetFormattedDuration(),
                Icon = "clock-circle"
            });
        }

        if (loop.TokenUsage != null)
        {
            items.Add(new MessageHeader.MessageMetaItem
            {
                Title = "Token 使用量",
                Value = loop.TokenUsage.TotalTokens.ToString("N0"),
                Icon = "calculator"
            });
        }

        return items;
    }
}
