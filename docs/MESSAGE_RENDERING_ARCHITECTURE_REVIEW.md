# 消息渲染管线架构审阅与重构方案

## 一、现有架构分析

### 1.1 组件层级结构

```
MessageList.razor                    # 顶层消息列表容器
├── ChatMessageItem.razor            # 单条消息项（用户/工具/系统）
│   ├── MessageHeader.razor          # 消息头部（角色、时间、状态）
│   └── ContentFlow.razor            # 内容流容器
│       └── ContentBlockRenderer.razor # 内容块渲染器
│           ├── ReasoningBlock       # 思考过程
│           ├── TextBlock            # 文本内容
│           ├── ToolCallCard         # 工具调用
│           ├── AttachmentBlock      # 附件/图片
│           ├── ErrorBlock           # 错误信息
│           ├── SubAgentBlock        # 子代理
│           ├── PermissionBlock      # 权限请求
│           └── DividerBlock         # 分隔线
└── LoopGroupItem.razor              # Loop分组消息
    ├── MessageHeader.razor
    └── ContentFlow.razor
        └── ContentBlockRenderer.razor
```

### 1.2 数据模型

```
MessageViewModel                     # 消息视图模型
├── ContentBlock                     # 内容块模型
│   ├── ContentBlockType            # 块类型枚举
│   └── ContentBlockBuilder         # 块构建器（静态工厂）
├── LoopGroupViewModel              # Loop分组模型
├── ToolCallViewModel               # 工具调用模型
└── MessageRenderContext            # 渲染上下文
```

### 1.3 策略模式

```
IMessageRenderStrategy              # 渲染策略接口
├── UserMessageStrategy             # 用户消息策略
├── AssistantMessageStrategy        # 助手消息策略
├── SystemMessageStrategy           # 系统消息策略
├── ToolMessageStrategy             # 工具消息策略
├── ErrorMessageStrategy            # 错误消息策略
└── DefaultMessageStrategy          # 默认策略

MessageRenderStrategyProvider       # 策略提供者
MessageComponentFactory             # 组件工厂（静态方法）
```

---

## 二、现有问题诊断

### 2.1 架构层面问题

#### 问题 P1: 双重渲染路径不一致
**严重程度**: 🔴 高

存在两套独立的渲染路径：
- `ChatMessageItem` → `ContentFlow` → `ContentBlockRenderer`
- `LoopGroupItem` → `ContentFlow` → `ContentBlockRenderer`

两条路径在 `MessageList.razor` 中通过条件判断选择，导致：
- 渲染逻辑分散
- 状态管理不一致
- 代码重复

```razor
<!-- MessageList.razor 中的分支逻辑 -->
@if (message.Role == "user")
{
    <ChatMessageItem ... />
}
else if (message.Role == "assistant")
{
    @if (IsFirstAssistantInLoop(message))
    {
        <LoopGroupItem ... />
    }
}
```

#### 问题 P2: 职责边界模糊
**严重程度**: 🟡 中

- `ContentBlockBuilder`: 静态类，承担了构建、排序、ID生成
- `ContentBlockRenderer`: 承担了渲染逻辑 + 状态管理 + Markdown缓存
- `MessageComponentFactory`: 静态工厂方法，与策略模式重叠

#### 问题 P3: 缺乏统一的渲染管线抽象
**严重程度**: 🔴 高

没有统一的渲染管线接口：
```csharp
// 缺失的抽象
interface IMessageRenderPipeline
{
    RenderResult Render(MessageViewModel message);
    RenderResult RenderLoop(LoopGroupViewModel loop);
}
```

### 2.2 性能问题

#### 问题 P4: 内容块重复构建
**严重程度**: 🟡 中

每次 `OnParametersSet` 都可能重建内容块：
```csharp
// ChatMessageItem.razor
protected override void OnParametersSet()
{
    if (Message.Id != _lastMessageId || !_isComplete)
    {
        _contentBlocks = ContentBlockBuilder.BuildFromMessage(Message);
    }
}
```

虽然做了缓存检查，但：
- 缓存粒度是整个消息
- 流式更新时每次都重建

#### 问题 P5: Markdown 重复渲染
**严重程度**: 🟢 低

每个组件内部都有独立的 Markdown 缓存：
```csharp
// ContentBlockRenderer.razor
private string _cachedContent = string.Empty;
private string _cachedHtml = string.Empty;
```

问题：
- 缓存不共享
- 相同内容在不同组件会重复渲染

#### 问题 P6: Loop 缓存复杂度高
**严重程度**: 🟡 中

`MessageList.razor` 内置了复杂的缓存系统：
```csharp
private readonly Dictionary<string, int> _loopIndexMap = new();
private readonly Dictionary<string, LoopGroupViewModel> _loopCache = new();
private readonly Dictionary<string, List<MessageViewModel>> _loopMessages = new();
private readonly HashSet<string> _processedUserMessageIds = new();
```

多个字典需要同步维护，容易出错。

### 2.3 扩展性问题

#### 问题 P7: 内容块类型硬编码
**严重程度**: 🟡 中

`ContentBlockRenderer` 使用 switch-case 分发：
```csharp
@switch (Block.Type)
{
    case ContentBlockType.Reasoning:
        @RenderReasoningBlock()
        break;
    case ContentBlockType.Text:
        @RenderTextBlock()
        break;
    // ...
}
```

添加新类型需要：
1. 修改枚举 `ContentBlockType`
2. 修改 `ContentBlockBuilder`
3. 修改 `ContentBlockRenderer`

#### 问题 P8: 渲染策略与内容块渲染耦合
**严重程度**: 🟡 中

策略模式只决定"显示什么"，不决定"怎么渲染"：
```csharp
public interface IMessageRenderStrategy
{
    bool ShouldShowReasoning(MessageViewModel message);  // 是否显示
    bool ShouldShowToolCalls(MessageViewModel message);  // 是否显示
    // 但没有如何渲染的抽象
}
```

### 2.4 状态管理问题

#### 问题 P9: 组件内部状态与参数混用
**严重程度**: 🟢 低

```csharp
// ContentBlockRenderer.razor
private bool _isReasoningExpanded;
private bool _reasoningInitialized;

// 推理块展开逻辑混合了参数和内部状态
private bool IsReasoningExpanded => 
    Block?.Type == ContentBlockType.Reasoning && 
    (Block.IsStreaming || _isReasoningExpanded);
```

#### 问题 P10: 流式状态管理分散
**严重程度**: 🟡 中

`IsStreaming`/`IsComplete` 在多个层级判断：
- `ContentBlock.IsStreaming`
- `MessageViewModel.IsComplete`
- `LoopGroupViewModel.IsExecuting`

缺乏统一的流式状态管理。

---

## 三、重构目标

### 3.1 设计原则

1. **单一职责原则 (SRP)**: 每个组件/类只做一件事
2. **开闭原则 (OCP)**: 对扩展开放，对修改关闭
3. **依赖倒置原则 (DIP)**: 依赖抽象而非具体实现
4. **接口隔离原则 (ISP)**: 接口最小化

### 3.2 目标架构

```
┌─────────────────────────────────────────────────────────────┐
│                    MessageRenderPipeline                     │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐  │
│  │   Message   │  │   Context   │  │   RendererRegistry  │  │
│  │  Preprocess │→ │   Builder   │→ │   (Block→Renderer)  │  │
│  └─────────────┘  └─────────────┘  └─────────────────────┘  │
│                          ↓                                  │
│  ┌─────────────────────────────────────────────────────┐    │
│  │                  RenderContext                       │    │
│  │  ┌───────────┐ ┌───────────┐ ┌───────────────────┐  │    │
│  │  │  Message  │ │  Blocks   │ │  RenderOptions    │  │    │
│  │  └───────────┘ └───────────┘ └───────────────────┘  │    │
│  └─────────────────────────────────────────────────────┘    │
│                          ↓                                  │
│  ┌─────────────────────────────────────────────────────┐    │
│  │               IContentBlockRenderer<T>               │    │
│  │  ┌─────────┐ ┌─────────┐ ┌─────────┐ ┌─────────┐   │    │
│  │  │Reasoning│ │  Text   │ │ToolCall │ │  Image  │   │    │
│  │  │Renderer │ │Renderer │ │Renderer │ │Renderer │   │    │
│  │  └─────────┘ └─────────┘ └─────────┘ └─────────┘   │    │
│  └─────────────────────────────────────────────────────┘    │
└─────────────────────────────────────────────────────────────┘
```

---

## 四、重构方案

### 4.1 核心抽象层

#### 4.1.1 渲染管线接口

```csharp
namespace Seeing.Agent.WebUI.Rendering.Abstractions;

/// <summary>
/// 内容块渲染器接口 - 泛型版本，支持类型安全
/// </summary>
public interface IContentBlockRenderer<TBlock> where TBlock : ContentBlock
{
    /// <summary>
    /// 渲染器支持的块类型
    /// </summary>
    ContentBlockType BlockType { get; }
    
    /// <summary>
    /// 渲染优先级（数值越小优先级越高）
    /// </summary>
    int Priority { get; }
    
    /// <summary>
    /// 渲染内容块
    /// </summary>
    RenderFragment Render(TBlock block, RenderContext context);
    
    /// <summary>
    /// 是否可以处理该块
    /// </summary>
    bool CanRender(ContentBlock block);
}

/// <summary>
/// 渲染上下文 - 包含渲染所需的所有信息
/// </summary>
public class RenderContext
{
    /// <summary>
    /// 当前消息 ID
    /// </summary>
    public string MessageId { get; init; } = string.Empty;
    
    /// <summary>
    /// 当前 Loop ID（如果有）
    /// </summary>
    public string? LoopId { get; init; }
    
    /// <summary>
    /// 是否流式渲染
    /// </summary>
    public bool IsStreaming { get; init; }
    
    /// <summary>
    /// 渲染选项
    /// </summary>
    public RenderOptions Options { get; init; } = new();
    
    /// <summary>
    /// 服务提供者（用于依赖注入）
    /// </summary>
    public IServiceProvider? ServiceProvider { get; init; }
    
    /// <summary>
    /// 缓存服务
    /// </summary>
    public IRenderCache? Cache { get; init; }
}

/// <summary>
/// 渲染选项
/// </summary>
public class RenderOptions
{
    /// <summary>
    /// 是否显示推理过程
    /// </summary>
    public bool ShowReasoning { get; set; } = true;
    
    /// <summary>
    /// 是否默认展开推理
    /// </summary>
    public bool ExpandReasoningByDefault { get; set; } = false;
    
    /// <summary>
    /// Markdown 渲染选项
    /// </summary>
    public MarkdownRenderOptions Markdown { get; set; } = new();
    
    /// <summary>
    /// 工具调用显示选项
    /// </summary>
    public ToolCallRenderOptions ToolCalls { get; set; } = new();
}
```

#### 4.1.2 渲染器注册机制

```csharp
namespace Seeing.Agent.WebUI.Rendering;

/// <summary>
/// 内容块渲染器注册表
/// </summary>
public interface IContentBlockRendererRegistry
{
    /// <summary>
    /// 注册渲染器
    /// </summary>
    void Register<TBlock>(IContentBlockRenderer<TBlock> renderer) where TBlock : ContentBlock;
    
    /// <summary>
    /// 获取渲染器
    /// </summary>
    IContentBlockRenderer<ContentBlock>? GetRenderer(ContentBlockType type);
    
    /// <summary>
    /// 获取所有已注册的渲染器
    /// </summary>
    IEnumerable<IContentBlockRenderer<ContentBlock>> GetAllRenderers();
    
    /// <summary>
    /// 尝试渲染内容块
    /// </summary>
    bool TryRender(ContentBlock block, RenderContext context, out RenderFragment? fragment);
}

/// <summary>
/// 渲染器注册表实现
/// </summary>
public class ContentBlockRendererRegistry : IContentBlockRendererRegistry
{
    private readonly Dictionary<ContentBlockType, IContentBlockRenderer<ContentBlock>> _renderers = new();
    private readonly ILogger<ContentBlockRendererRegistry> _logger;
    
    public ContentBlockRendererRegistry(
        IEnumerable<IContentBlockRenderer<ContentBlock>> renderers,
        ILogger<ContentBlockRendererRegistry> logger)
    {
        _logger = logger;
        
        // 自动注册所有注入的渲染器
        foreach (var renderer in renderers)
        {
            Register(renderer);
        }
    }
    
    public void Register<TBlock>(IContentBlockRenderer<TBlock> renderer) 
        where TBlock : ContentBlock
    {
        var blockType = renderer.BlockType;
        
        if (_renderers.ContainsKey(blockType))
        {
            _logger.LogWarning(
                "Renderer for {BlockType} already registered, replacing with {RendererType}",
                blockType, renderer.GetType().Name);
        }
        
        _renderers[blockType] = (IContentBlockRenderer<ContentBlock>)renderer;
        _logger.LogDebug("Registered renderer {RendererType} for {BlockType}",
            renderer.GetType().Name, blockType);
    }
    
    public IContentBlockRenderer<ContentBlock>? GetRenderer(ContentBlockType type)
    {
        return _renderers.TryGetValue(type, out var renderer) ? renderer : null;
    }
    
    public IEnumerable<IContentBlockRenderer<ContentBlock>> GetAllRenderers()
    {
        return _renderers.Values.OrderBy(r => r.Priority);
    }
    
    public bool TryRender(ContentBlock block, RenderContext context, out RenderFragment? fragment)
    {
        if (_renderers.TryGetValue(block.Type, out var renderer))
        {
            try
            {
                fragment = renderer.Render(block, context);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error rendering block {BlockType}", block.Type);
                fragment = null;
                return false;
            }
        }
        
        fragment = null;
        return false;
    }
}
```

### 4.2 渲染缓存服务

```csharp
namespace Seeing.Agent.WebUI.Rendering.Caching;

/// <summary>
/// 渲染缓存接口
/// </summary>
public interface IRenderCache
{
    /// <summary>
    /// 获取或创建缓存项
    /// </summary>
    string GetOrCreateMarkdown(string content, string cacheKey);
    
    /// <summary>
    /// 使缓存失效
    /// </summary>
    void Invalidate(string cacheKey);
    
    /// <summary>
    /// 清空所有缓存
    /// </summary>
    void Clear();
    
    /// <summary>
    /// 获取缓存统计
    /// </summary>
    CacheStatistics GetStatistics();
}

/// <summary>
/// 渲染缓存实现
/// </summary>
public class MemoryRenderCache : IRenderCache
{
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly MarkdownPipeline _markdownPipeline;
    private readonly ILogger<MemoryRenderCache> _logger;
    private readonly TimeSpan _expiration = TimeSpan.FromMinutes(30);
    
    public MemoryRenderCache(ILogger<MemoryRenderCache> logger)
    {
        _logger = logger;
        _markdownPipeline = new MarkdownPipelineBuilder()
            .UseAutoLinks()
            .UseTaskLists()
            .UsePipeTables()
            .Build();
    }
    
    public string GetOrCreateMarkdown(string content, string cacheKey)
    {
        var entry = _cache.GetOrAdd(cacheKey, key => new CacheEntry
        {
            Content = content,
            RenderedHtml = Markdown.ToHtml(content, _markdownPipeline),
            CreatedAt = DateTime.UtcNow
        });
        
        // 检查内容是否变化
        if (entry.Content != content)
        {
            entry.Content = content;
            entry.RenderedHtml = Markdown.ToHtml(content, _markdownPipeline);
            entry.CreatedAt = DateTime.UtcNow;
        }
        
        return entry.RenderedHtml;
    }
    
    public void Invalidate(string cacheKey)
    {
        _cache.TryRemove(cacheKey, out _);
    }
    
    public void Clear()
    {
        _cache.Clear();
    }
    
    public CacheStatistics GetStatistics()
    {
        return new CacheStatistics
        {
            Count = _cache.Count,
            TotalSize = _cache.Values.Sum(e => e.Content?.Length ?? 0)
        };
    }
    
    private class CacheEntry
    {
        public string? Content { get; set; }
        public string RenderedHtml { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
    }
}

public record CacheStatistics
{
    public int Count { get; init; }
    public long TotalSize { get; init; }
}
```

### 4.3 统一渲染管线

```csharp
namespace Seeing.Agent.WebUI.Rendering;

/// <summary>
/// 消息渲染管线接口
/// </summary>
public interface IMessageRenderPipeline
{
    /// <summary>
    /// 渲染单条消息
    /// </summary>
    RenderFragment RenderMessage(MessageViewModel message, RenderOptions? options = null);
    
    /// <summary>
    /// 渲染 Loop 分组
    /// </summary>
    RenderFragment RenderLoop(LoopGroupViewModel loop, RenderOptions? options = null);
    
    /// <summary>
    /// 渲染消息列表
    /// </summary>
    RenderFragment RenderMessageList(
        IEnumerable<MessageViewModel> messages, 
        MessageListOptions? options = null);
}

/// <summary>
/// 消息渲染管线实现
/// </summary>
public class MessageRenderPipeline : IMessageRenderPipeline
{
    private readonly IContentBlockRendererRegistry _rendererRegistry;
    private readonly IRenderCache _cache;
    private readonly ILogger<MessageRenderPipeline> _logger;
    
    public MessageRenderPipeline(
        IContentBlockRendererRegistry rendererRegistry,
        IRenderCache cache,
        ILogger<MessageRenderPipeline> logger)
    {
        _rendererRegistry = rendererRegistry;
        _cache = cache;
        _logger = logger;
    }
    
    public RenderFragment RenderMessage(MessageViewModel message, RenderOptions? options = null)
    {
        var context = CreateContext(message, options);
        var blocks = ContentBlockBuilder.BuildFromMessage(message);
        
        return builder =>
        {
            var seq = 0;
            
            // 渲染消息头部
            builder.OpenComponent<MessageHeader>(seq++);
            builder.AddAttribute(seq++, "Role", message.Role);
            builder.AddAttribute(seq++, "Timestamp", message.Timestamp);
            builder.AddAttribute(seq++, "Status", DetermineStatus(message));
            builder.CloseComponent();
            
            // 渲染内容块
            foreach (var block in blocks.OrderBy(b => b.SortIndex))
            {
                if (_rendererRegistry.TryRender(block, context, out var fragment))
                {
                    builder.AddContent(seq++, fragment);
                }
            }
        };
    }
    
    public RenderFragment RenderLoop(LoopGroupViewModel loop, RenderOptions? options = null)
    {
        var context = CreateContext(loop.Messages.FirstOrDefault(), options)
            with { LoopId = loop.LoopId };
        
        var blocks = ContentBlockBuilder.BuildFromLoopMessages(loop.Messages);
        
        return builder =>
        {
            var seq = 0;
            
            // 渲染 Loop 头部
            builder.OpenComponent<MessageHeader>(seq++);
            builder.AddAttribute(seq++, "Role", "assistant");
            builder.AddAttribute(seq++, "Timestamp", loop.StartTime ?? DateTime.Now);
            builder.AddAttribute(seq++, "Status", DetermineLoopStatus(loop));
            builder.AddAttribute(seq++, "Tags", BuildLoopTags(loop));
            builder.CloseComponent();
            
            // 渲染内容块
            foreach (var block in blocks.OrderBy(b => b.SortIndex))
            {
                if (_rendererRegistry.TryRender(block, context, out var fragment))
                {
                    builder.AddContent(seq++, fragment);
                }
            }
        };
    }
    
    public RenderFragment RenderMessageList(
        IEnumerable<MessageViewModel> messages, 
        MessageListOptions? options = null)
    {
        options ??= new MessageListOptions();
        
        return builder =>
        {
            var seq = 0;
            
            builder.OpenElement(seq++, "div");
            builder.AddAttribute(seq++, "class", "message-list-container");
            
            // 按角色分组渲染
            var processedLoopIds = new HashSet<string>();
            
            foreach (var message in messages)
            {
                if (message.Role == "assistant" && !string.IsNullOrEmpty(message.LoopId))
                {
                    // 避免同一 Loop 重复渲染
                    if (processedLoopIds.Contains(message.LoopId))
                        continue;
                    processedLoopIds.Add(message.LoopId);
                    
                    // 这里需要从外部获取 LoopGroupViewModel
                    // 或者提供 Loop 构建器接口
                }
                
                builder.AddContent(seq++, RenderMessage(message, options.RenderOptions));
            }
            
            builder.CloseElement();
        };
    }
    
    private RenderContext CreateContext(MessageViewModel? message, RenderOptions? options)
    {
        return new RenderContext
        {
            MessageId = message?.Id ?? string.Empty,
            LoopId = message?.LoopId,
            IsStreaming = message?.IsComplete == false,
            Options = options ?? new RenderOptions(),
            Cache = _cache
        };
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
        
        return tags;
    }
}

/// <summary>
/// 消息列表渲染选项
/// </summary>
public class MessageListOptions
{
    public RenderOptions RenderOptions { get; set; } = new();
    public bool AutoScroll { get; set; } = true;
    public bool ShowTimestamp { get; set; } = true;
}
```

### 4.4 内容块渲染器实现示例

```csharp
namespace Seeing.Agent.WebUI.Rendering.Renderers;

/// <summary>
/// 文本内容块渲染器
/// </summary>
public class TextBlockRenderer : IContentBlockRenderer<ContentBlock>
{
    public ContentBlockType BlockType => ContentBlockType.Text;
    public int Priority => 100;
    
    public RenderFragment Render(ContentBlock block, RenderContext context)
    {
        return builder =>
        {
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "class", "content-block-text message-content");
            
            var html = context.Cache?.GetOrCreateMarkdown(
                block.Content ?? string.Empty, 
                $"text-{context.MessageId}-{block.SortIndex}")
                ?? block.Content ?? string.Empty;
            
            builder.AddContent(2, new MarkupString(html));
            builder.CloseElement();
        };
    }
    
    public bool CanRender(ContentBlock block) => block.Type == ContentBlockType.Text;
}

/// <summary>
/// 推理内容块渲染器
/// </summary>
public class ReasoningBlockRenderer : IContentBlockRenderer<ContentBlock>
{
    public ContentBlockType BlockType => ContentBlockType.Reasoning;
    public int Priority => 10; // 高优先级，推理块显示在最前
    
    public RenderFragment Render(ContentBlock block, RenderContext context)
    {
        return builder =>
        {
            var isExpanded = context.IsStreaming || context.Options.ExpandReasoningByDefault;
            
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "class", "content-block-reasoning");
            
            // 头部
            builder.OpenElement(2, "div");
            builder.AddAttribute(3, "class", "reasoning-header");
            // ... 渲染头部内容
            
            // 内容
            if (isExpanded)
            {
                builder.OpenElement(4, "div");
                builder.AddAttribute(5, "class", "reasoning-content");
                
                var html = context.Cache?.GetOrCreateMarkdown(
                    block.Content ?? string.Empty,
                    $"reasoning-{context.MessageId}")
                    ?? block.Content ?? string.Empty;
                
                builder.AddContent(6, new MarkupString(html));
                builder.CloseElement();
            }
            
            builder.CloseElement();
        };
    }
    
    public bool CanRender(ContentBlock block) => block.Type == ContentBlockType.Reasoning;
}

/// <summary>
/// 工具调用内容块渲染器
/// </summary>
public class ToolCallBlockRenderer : IContentBlockRenderer<ContentBlock>
{
    public ContentBlockType BlockType => ContentBlockType.ToolCall;
    public int Priority => 50;
    
    public RenderFragment Render(ContentBlock block, RenderContext context)
    {
        var toolCall = block.ToolCall;
        if (toolCall == null)
            return _ => { };
        
        return builder =>
        {
            builder.OpenComponent<ToolCallCard>(0);
            builder.AddAttribute(1, "ToolCall", toolCall);
            builder.CloseComponent();
        };
    }
    
    public bool CanRender(ContentBlock block) => 
        block.Type == ContentBlockType.ToolCall && block.ToolCall != null;
}
```

### 4.5 依赖注入配置

```csharp
namespace Seeing.Agent.WebUI.Rendering;

public static class RenderingServiceExtensions
{
    public static IServiceCollection AddMessageRendering(
        this IServiceCollection services)
    {
        // 注册缓存服务
        services.AddSingleton<IRenderCache, MemoryRenderCache>();
        
        // 注册渲染器注册表
        services.AddSingleton<IContentBlockRendererRegistry, ContentBlockRendererRegistry>();
        
        // 注册所有内置渲染器
        services.AddSingleton<IContentBlockRenderer<ContentBlock>, TextBlockRenderer>();
        services.AddSingleton<IContentBlockRenderer<ContentBlock>, ReasoningBlockRenderer>();
        services.AddSingleton<IContentBlockRenderer<ContentBlock>, ToolCallBlockRenderer>();
        services.AddSingleton<IContentBlockRenderer<ContentBlock>, ImageBlockRenderer>();
        services.AddSingleton<IContentBlockRenderer<ContentBlock>, AttachmentBlockRenderer>();
        services.AddSingleton<IContentBlockRenderer<ContentBlock>, ErrorBlockRenderer>();
        services.AddSingleton<IContentBlockRenderer<ContentBlock>, SubAgentBlockRenderer>();
        services.AddSingleton<IContentBlockRenderer<ContentBlock>, PermissionBlockRenderer>();
        services.AddSingleton<IContentBlockRenderer<ContentBlock>, DividerBlockRenderer>();
        
        // 注册渲染管线
        services.AddScoped<IMessageRenderPipeline, MessageRenderPipeline>();
        
        return services;
    }
}
```

---

## 五、重构后架构优势

### 5.1 解决的问题

| 问题 | 解决方案 | 状态 |
|------|----------|------|
| P1: 双重渲染路径 | 统一 `IMessageRenderPipeline` | ✅ |
| P2: 职责边界模糊 | 清晰的接口分层 | ✅ |
| P3: 缺乏抽象 | `IContentBlockRenderer<T>` | ✅ |
| P4: 内容块重复构建 | 管线内统一构建，缓存共享 | ✅ |
| P5: Markdown 重复渲染 | `IRenderCache` 集中管理 | ✅ |
| P6: Loop 缓存复杂 | 简化为单一职责 | ✅ |
| P7: 内容块类型硬编码 | 注册机制，支持运行时扩展 | ✅ |
| P8: 策略与渲染耦合 | `RenderOptions` 配置化 | ✅ |
| P9: 状态管理混乱 | `RenderContext` 统一上下文 | ✅ |
| P10: 流式状态分散 | `IsStreaming` 集中管理 | ✅ |

### 5.2 扩展能力

#### 添加新的内容块类型

```csharp
// 1. 定义新的内容块类型
public class CodeArtifactBlock : ContentBlock
{
    public string? Language { get; set; }
    public string? Code { get; set; }
}

// 2. 实现渲染器
public class CodeArtifactRenderer : IContentBlockRenderer<CodeArtifactBlock>
{
    public ContentBlockType BlockType => (ContentBlockType)100; // 自定义类型
    public int Priority => 60;
    
    public RenderFragment Render(CodeArtifactBlock block, RenderContext context)
    {
        // 渲染逻辑
    }
    
    public bool CanRender(ContentBlock block) => block is CodeArtifactBlock;
}

// 3. 注册渲染器（自动通过 DI 注册）
services.AddSingleton<IContentBlockRenderer<ContentBlock>, CodeArtifactRenderer>();
```

#### 自定义渲染行为

```csharp
// 在页面中自定义渲染选项
var options = new RenderOptions
{
    ShowReasoning = false,  // 隐藏推理过程
    ExpandReasoningByDefault = true,
    Markdown = new MarkdownRenderOptions
    {
        EnableSyntaxHighlighting = true,
        EnableMath = true
    }
};

await pipeline.RenderMessage(message, options);
```

### 5.3 性能优化

- **Markdown 缓存**: 全局共享，避免重复渲染
- **渲染器缓存**: 渲染器单例，减少对象创建
- **增量渲染**: 流式更新时只重建变化的内容块

---

## 六、迁移计划

### 6.1 阶段一：基础设施（1-2天）

1. 创建 `Rendering.Abstractions` 命名空间
2. 实现 `IRenderCache` 和 `MemoryRenderCache`
3. 实现 `IContentBlockRendererRegistry`

### 6.2 阶段二：渲染器迁移（2-3天）

1. 将现有 `ContentBlockRenderer` 拆分为独立渲染器
2. 实现所有内置渲染器
3. 配置依赖注入

### 6.3 阶段三：管线集成（1-2天）

1. 实现 `IMessageRenderPipeline`
2. 重构 `MessageList` 使用管线
3. 简化 `LoopGroupItem` 和 `ChatMessageItem`

### 6.4 阶段四：测试验证（1天）

1. 单元测试渲染器
2. 集成测试管线
3. 性能基准测试

---

## 七、总结

本次重构通过引入统一的渲染管线抽象，解决了现有架构中的职责不清、扩展困难、性能隐患等问题。新架构具有以下特点：

1. **清晰的分层**: 抽象层 → 实现层 → 应用层
2. **高度可扩展**: 通过注册机制添加新渲染器
3. **性能优化**: 全局缓存、渲染器复用
4. **易于测试**: 接口驱动，依赖注入

这将为后续添加新功能（如代码沙箱、图表渲染、自定义组件）提供坚实基础。
