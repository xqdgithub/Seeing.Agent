using Seeing.Agent.WebUI.Models.Messaging;
using Seeing.Agent.WebUI.Rendering.Abstractions;
using System.Collections.Concurrent;

namespace Seeing.Agent.WebUI.Rendering.Components;

/// <summary>
/// 消息组件注册表接口
/// </summary>
public interface IMessageComponentRegistry
{
    /// <summary>
    /// 注册消息组件
    /// </summary>
    /// <param name="component">消息组件</param>
    void Register(IMessageComponent component);

    /// <summary>
    /// 尝试获取可以渲染指定内容块的组件
    /// </summary>
    /// <param name="block">内容块</param>
    /// <param name="component">消息组件</param>
    /// <returns>是否找到可用组件</returns>
    bool TryGetComponent(ContentBlock block, out IMessageComponent? component);

    /// <summary>
    /// 获取所有已注册的组件
    /// </summary>
    /// <returns>组件列表</returns>
    IReadOnlyList<IMessageComponent> GetAllComponents();
}

/// <summary>
/// 消息组件注册表实现
/// </summary>
/// <remarks>
/// <para>
/// 线程安全的组件注册表，支持：
/// <list type="bullet">
///   <item><description>按内容块类型注册组件</description></item>
///   <item><description>支持优先级排序</description></item>
///   <item><description>支持动态注册</description></item>
/// </list>
/// </para>
/// </remarks>
public class MessageComponentRegistry : IMessageComponentRegistry
{
    private readonly ConcurrentDictionary<ContentBlockType, List<IMessageComponent>> _components = new();
    private readonly ILogger<MessageComponentRegistry> _logger;

    /// <summary>
    /// 创建消息组件注册表，并自动注册所有通过 DI 注入的 IMessageComponent
    /// </summary>
    /// <param name="components">所有通过 DI 注册的 IMessageComponent 实例</param>
    /// <param name="logger">日志器</param>
    public MessageComponentRegistry(
        IEnumerable<IMessageComponent> components,
        ILogger<MessageComponentRegistry> logger)
    {
        _logger = logger;

        // 自动注册所有通过 DI 注入的组件
        foreach (var component in components.OrderBy(c => c.Priority))
        {
            Register(component);
        }

        _logger.LogInformation(
            "消息组件注册表已初始化，已注册 {Count} 个组件: {Components}",
            _components.Values.Sum(v => v.Count),
            string.Join(", ", components.Select(c => $"{c.Name}({c.BlockType}:{c.Priority})")));
    }

    /// <inheritdoc/>
    public void Register(IMessageComponent component)
    {
        if (component == null)
            throw new ArgumentNullException(nameof(component));

        var components = _components.GetOrAdd(component.BlockType, _ => new List<IMessageComponent>());

        lock (components)
        {
            // 检查是否已注册同名组件
            var existing = components.FirstOrDefault(c => c.Name == component.Name);
            if (existing != null)
            {
                _logger.LogWarning("消息组件 '{Name}' 已注册，将被替换", component.Name);
                components.Remove(existing);
            }

            // 添加组件并按优先级排序
            components.Add(component);
            components.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        }

        _logger.LogDebug("注册消息组件 '{Name}' (类型: {Type}, 优先级: {Priority})",
            component.Name, component.BlockType, component.Priority);
    }

    /// <inheritdoc/>
    public bool TryGetComponent(ContentBlock block, out IMessageComponent? component)
    {
        component = null;

        if (!_components.TryGetValue(block.Type, out var components))
            return false;

        lock (components)
        {
            // 按优先级顺序查找可渲染的组件
            foreach (var comp in components)
            {
                if (comp.CanRender(block))
                {
                    component = comp;
                    return true;
                }
            }
        }

        return false;
    }

    /// <inheritdoc/>
    public IReadOnlyList<IMessageComponent> GetAllComponents()
    {
        var allComponents = new List<IMessageComponent>();

        foreach (var kvp in _components)
        {
            lock (kvp.Value)
            {
                allComponents.AddRange(kvp.Value);
            }
        }

        return allComponents.AsReadOnly();
    }
}

/// <summary>
/// 消息组件注册表扩展方法
/// </summary>
public static class MessageComponentRegistryExtensions
{
    /// <summary>
    /// 注册文本消息组件
    /// </summary>
    public static IMessageComponentRegistry RegisterTextComponent<TComponent>(
        this IMessageComponentRegistry registry,
        Func<TComponent, string>? contentGetter = null,
        Func<TComponent, ContentBlock>? blockGetter = null,
        Func<TComponent, RenderContext>? contextGetter = null,
        int priority = 100) where TComponent : Microsoft.AspNetCore.Components.IComponent
    {
        registry.Register(new DefaultMessageComponent<TComponent>(
            ContentBlockType.Text,
            priority,
            "Text",
            block => block.Type == ContentBlockType.Text && block.Content != null,
            (block, context) => new Dictionary<string, object?>
            {
                ["Content"] = block.Content ?? string.Empty,
                ["Block"] = block,
                ["Context"] = context
            }));

        return registry;
    }

    /// <summary>
    /// 注册推理消息组件
    /// </summary>
    public static IMessageComponentRegistry RegisterReasoningComponent<TComponent>(
        this IMessageComponentRegistry registry,
        int priority = 10) where TComponent : Microsoft.AspNetCore.Components.IComponent
    {
        registry.Register(new DefaultMessageComponent<TComponent>(
            ContentBlockType.Reasoning,
            priority,
            "Reasoning",
            block => block.Type == ContentBlockType.Reasoning,
            (block, context) => new Dictionary<string, object?>
            {
                ["Content"] = block.Content ?? string.Empty,
                ["Block"] = block,
                ["Context"] = context
            }));

        return registry;
    }

    /// <summary>
    /// 注册工具调用消息组件
    /// </summary>
    public static IMessageComponentRegistry RegisterToolCallComponent<TComponent>(
        this IMessageComponentRegistry registry,
        int priority = 50) where TComponent : Microsoft.AspNetCore.Components.IComponent
    {
        registry.Register(new DefaultMessageComponent<TComponent>(
            ContentBlockType.ToolCall,
            priority,
            "ToolCall",
            block => block.Type == ContentBlockType.ToolCall && block.ToolCall != null,
            (block, context) => new Dictionary<string, object?>
            {
                ["ToolCall"] = block.ToolCall!,
                ["Block"] = block,
                ["Context"] = context
            }));

        return registry;
    }
}

/// <summary>
/// 默认消息组件实现
/// </summary>
/// <typeparam name="TComponent">组件类型</typeparam>
public class DefaultMessageComponent<TComponent> : IMessageComponent where TComponent : Microsoft.AspNetCore.Components.IComponent
{
    /// <inheritdoc/>
    public ContentBlockType BlockType { get; }

    /// <inheritdoc/>
    public int Priority { get; }

    /// <inheritdoc/>
    public string Name { get; }

    private readonly Func<ContentBlock, bool> _canRender;
    private readonly Func<ContentBlock, RenderContext, Dictionary<string, object?>> _getParameters;

    public DefaultMessageComponent(
        ContentBlockType blockType,
        int priority,
        string name,
        Func<ContentBlock, bool> canRender,
        Func<ContentBlock, RenderContext, Dictionary<string, object?>> getParameters)
    {
        BlockType = blockType;
        Priority = priority;
        Name = name;
        _canRender = canRender ?? throw new ArgumentNullException(nameof(canRender));
        _getParameters = getParameters ?? throw new ArgumentNullException(nameof(getParameters));
    }

    /// <inheritdoc/>
    public bool CanRender(ContentBlock block) => _canRender(block);

    /// <inheritdoc/>
    public Type GetComponentType() => typeof(TComponent);

    /// <inheritdoc/>
    public Dictionary<string, object?> GetComponentParameters(ContentBlock block, RenderContext context)
        => _getParameters(block, context);
}
