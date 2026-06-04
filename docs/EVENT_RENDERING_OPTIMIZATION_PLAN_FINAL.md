# AgentLoop 事件推送与渲染架构优化方案 - 最终版

> 版本: 2.0 (审查修订版)  
> 日期: 2025-01-15  
> 状态: 已审查  
> 审查专家: Oracle (事件推送) + Oracle (渲染架构)

---

## 一、审查结论摘要

### 事件推送方案评分: 4/5 ⭐⭐⭐⭐
- 事件完整性: 4/5
- 时序正确性: 4/5
- 向后兼容性: 4/5
- 性能影响: 4/5
- 代码质量: 4/5

### 渲染架构方案评分: 3.2/5 ⭐⭐⭐
- 渲染正确性: 4/5
- 性能优化: 2/5 (需重大修正)
- 扩展性: 4/5
- 用户体验: 3/5
- 代码质量: 3/5

---

## 二、必须修复的问题

### 2.1 高优先级问题 (P0)

#### [P0-001] PermissionRequestEvent 定义冲突
**严重程度**: 高  
**问题**: WebUI 已定义 `PermissionRequestEvent`，与方案中 Core 层定义冲突

**解决方案**:
```csharp
// 文件: src/Seeing.Agent/Core/Events/MessageEventTypes.cs
// 统一定义，删除 WebUI 中的重复定义

/// <summary>
/// 权限请求事件 - 统一定义
/// </summary>
public record PermissionRequestEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public string? LoopId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public MessageEventType Type => MessageEventType.PermissionRequest;
    
    /// <summary>权限请求 ID</summary>
    public required string PermissionId { get; init; }
    
    /// <summary>权限类型: tool, file, network, shell, agent</summary>
    public required string PermissionKind { get; init; }
    
    /// <summary>资源标识（工具名/文件路径等）</summary>
    public string? Resource { get; init; }
    
    /// <summary>请求参数（JSON）</summary>
    public object? Arguments { get; init; }
    
    /// <summary>风险等级: low, medium, high, critical</summary>
    public string RiskLevel { get; init; } = "medium";
    
    /// <summary>提示消息</summary>
    public string? Message { get; init; }
    
    /// <summary>超时时间（秒）</summary>
    public int TimeoutSeconds { get; init; } = 300;
}

// 文件: samples/Seeing.Agent.WebUI/Services/EventStreamHandler.cs
// 删除第 13-34 行的 PermissionRequestEvent 定义
// 使用 using Seeing.Agent.Core.Events; 引入统一定义
```

#### [P0-002] Loop 分组缓存方案根本性缺陷
**严重程度**: 高  
**问题**: 指纹计算本身是 O(n)，抵消了缓存收益

**解决方案**: 使用增量索引维护
```csharp
// 文件: samples/Seeing.Agent.WebUI/Components/MessageList.razor

@code {
    // ========== 增量索引缓存 ==========
    private Dictionary<string, int> _loopIndexMap = new();
    private Dictionary<string, LoopGroupViewModel> _loopCache = new();
    private Dictionary<string, List<MessageViewModel>> _loopMessages = new();
    private int _nextLoopIndex = 1;
    private int _lastMessageCount = 0;
    private const int MaxCacheSize = 100;
    
    /// <summary>
    /// 获取或创建 Loop 索引（O(1)）
    /// </summary>
    private int GetOrAddLoopIndex(string loopId)
    {
        if (!_loopIndexMap.TryGetValue(loopId, out var index))
        {
            index = _nextLoopIndex++;
            _loopIndexMap[loopId] = index;
        }
        return index;
    }
    
    /// <summary>
    /// 检查是否需要刷新（O(1)）
    /// </summary>
    private bool NeedsCacheRefresh()
    {
        return Messages.Count != _lastMessageCount;
    }
    
    /// <summary>
    /// 增量更新缓存
    /// </summary>
    private void UpdateCacheIncremental()
    {
        // 只处理新增的消息
        var newAssistantMessages = Messages
            .Skip(_lastMessageCount)
            .Where(m => m.Role == "assistant")
            .ToList();
        
        foreach (var msg in newAssistantMessages)
        {
            var loopId = msg.LoopId ?? $"single-{Messages.IndexOf(msg)}";
            
            // 更新消息分组
            if (!_loopMessages.ContainsKey(loopId))
            {
                _loopMessages[loopId] = new List<MessageViewModel>();
            }
            _loopMessages[loopId].Add(msg);
            
            // 分配索引
            GetOrAddLoopIndex(loopId);
        }
        
        // 更新受影响的 Loop 缓存
        var affectedLoopIds = newAssistantMessages
            .Select(m => m.LoopId ?? $"single-{Messages.IndexOf(m)}")
            .Distinct();
        
        foreach (var loopId in affectedLoopIds)
        {
            _loopCache[loopId] = BuildLoopViewModel(loopId);
        }
        
        _lastMessageCount = Messages.Count;
        
        // 清理超限缓存
        TrimCacheIfNeeded();
    }
    
    /// <summary>
    /// 构建 Loop 视图模型
    /// </summary>
    private LoopGroupViewModel BuildLoopViewModel(string loopId)
    {
        var messages = _loopMessages.GetValueOrDefault(loopId) ?? new List<MessageViewModel>();
        var loopIndex = _loopIndexMap.GetValueOrDefault(loopId, 0);
        
        return new LoopGroupViewModel
        {
            LoopId = loopId,
            LoopIndex = loopIndex,
            Messages = messages,
            IsComplete = messages.All(m => m.IsComplete),
            TotalSteps = messages.Count,
            StartTime = messages.FirstOrDefault()?.Timestamp,
            EndTime = messages.All(m => m.IsComplete) ? messages.LastOrDefault()?.Timestamp : null,
            Success = !messages.Any(m => m.ToolCalls.Any(t => !string.IsNullOrEmpty(t.Error)))
        };
    }
    
    /// <summary>
    /// 清理超限缓存
    /// </summary>
    private void TrimCacheIfNeeded()
    {
        if (_loopCache.Count > MaxCacheSize)
        {
            // 保留最近的 MaxCacheSize 个 Loop
            var toRemove = _loopIndexMap
                .OrderByDescending(kvp => kvp.Value)
                .Skip(MaxCacheSize)
                .Select(kvp => kvp.Key)
                .ToList();
            
            foreach (var key in toRemove)
            {
                _loopCache.Remove(key);
                _loopMessages.Remove(key);
                _loopIndexMap.Remove(key);
            }
        }
    }
    
    /// <summary>
    /// 获取 Loop 视图模型（带缓存）
    /// </summary>
    private LoopGroupViewModel GetOrBuildLoop(MessageViewModel message)
    {
        if (NeedsCacheRefresh())
        {
            UpdateCacheIncremental();
        }
        
        var loopId = message.LoopId ?? $"single-{Messages.IndexOf(message)}";
        
        return _loopCache.TryGetValue(loopId, out var cached) 
            ? cached 
            : BuildLoopViewModel(loopId);
    }
}
```

#### [P0-003] 内容块增量更新无法工作
**严重程度**: 高  
**问题**: `ContentBlock.Id` 每次随机生成，无法用于差异匹配

**解决方案**: 使用确定性 ID
```csharp
// 文件: samples/Seeing.Agent.WebUI/Models/Messaging/ContentBlock.cs

public partial class ContentBlock
{
    /// <summary>
    /// 生成确定性 ID
    /// </summary>
    private static string GenerateId(ContentBlockType type, int sortIndex, string? additionalKey = null)
    {
        return additionalKey != null 
            ? $"{type.ToString().ToLowerInvariant()}-{sortIndex}-{additionalKey}"
            : $"{type.ToString().ToLowerInvariant()}-{sortIndex}";
    }
    
    public static ContentBlock CreateReasoning(string? content, int sortIndex, bool isComplete = true)
    {
        return new ContentBlock
        {
            Id = GenerateId(ContentBlockType.Reasoning, sortIndex),
            Type = ContentBlockType.Reasoning,
            Content = content,
            SortIndex = sortIndex,
            IsComplete = isComplete,
            IsStreaming = !isComplete
        };
    }
    
    public static ContentBlock CreateText(string? content, int sortIndex, bool isComplete = true)
    {
        return new ContentBlock
        {
            Id = GenerateId(ContentBlockType.Text, sortIndex),
            Type = ContentBlockType.Text,
            Content = content,
            SortIndex = sortIndex,
            IsComplete = isComplete,
            IsStreaming = !isComplete
        };
    }
    
    public static ContentBlock CreateToolCall(ToolCallViewModel toolCall, int sortIndex)
    {
        return new ContentBlock
        {
            Id = GenerateId(ContentBlockType.ToolCall, sortIndex, toolCall.Id),
            Type = ContentBlockType.ToolCall,
            ToolCall = toolCall,
            SortIndex = sortIndex,
            IsComplete = toolCall.Status != "running"
        };
    }
    
    public static ContentBlock CreateAttachment(ContentPartViewModel attachment, int sortIndex)
    {
        return new ContentBlock
        {
            Id = GenerateId(ContentBlockType.Attachment, sortIndex, attachment.Id ?? Guid.NewGuid().ToString("N")[..8]),
            Type = attachment.IsImage ? ContentBlockType.Image : ContentBlockType.Attachment,
            Attachment = attachment,
            SortIndex = sortIndex
        };
    }
    
    // 新增类型
    public static ContentBlock CreateError(string error, int sortIndex, string? errorId = null)
    {
        return new ContentBlock
        {
            Id = GenerateId(ContentBlockType.Error, sortIndex, errorId),
            Type = ContentBlockType.Error,
            Content = error,
            SortIndex = sortIndex,
            IsComplete = true
        };
    }
    
    public static ContentBlock CreateSubAgent(string agentName, string? content, int sortIndex, bool isComplete = true)
    {
        return new ContentBlock
        {
            Id = GenerateId(ContentBlockType.SubAgent, sortIndex, agentName),
            Type = ContentBlockType.SubAgent,
            Content = content,
            SortIndex = sortIndex,
            IsComplete = isComplete,
            IsStreaming = !isComplete
        };
    }
    
    public static ContentBlock CreatePermission(PermissionRequestViewModel permission, int sortIndex)
    {
        return new ContentBlock
        {
            Id = GenerateId(ContentBlockType.Permission, sortIndex, permission.PermissionId),
            Type = ContentBlockType.Permission,
            SortIndex = sortIndex,
            IsComplete = false
        };
    }
    
    public static ContentBlock CreateDivider(int stepIndex, int sortIndex)
    {
        return new ContentBlock
        {
            Id = GenerateId(ContentBlockType.Divider, sortIndex),
            Type = ContentBlockType.Divider,
            SortIndex = sortIndex,
            IsComplete = true
        };
    }
}
```

#### [P0-004] ContentBlockRenderer 缺少新增类型渲染
**严重程度**: 高  
**问题**: 新增的 Error/SubAgent/Permission/Divider 类型无渲染分支

**解决方案**: 扩展渲染器
```razor
@* 文件: samples/Seeing.Agent.WebUI/Components/Messaging/ContentBlockRenderer.razor *@

@switch (Block.Type)
{
    case ContentBlockType.Reasoning:
        <ReasoningBlock Content="@Block.Content" 
                       IsStreaming="@Block.IsStreaming"
                       IsComplete="@Block.IsComplete" />
        break;
    
    case ContentBlockType.Text:
        <div class="content-text">
            @((MarkupString)RenderMarkdown(Block.Content ?? ""))
        </div>
        break;
    
    case ContentBlockType.ToolCall:
        <ToolCallCard ToolCall="@Block.ToolCall" 
                     OnClick="@OnToolClick" />
        break;
    
    case ContentBlockType.Attachment:
    case ContentBlockType.Image:
        <MessageAttachments Attachments="@new List<ContentPartViewModel> { Block.Attachment! }" />
        break;
    
    @* 新增类型渲染 *@
    case ContentBlockType.Error:
        @RenderErrorBlock()
        break;
    
    case ContentBlockType.SubAgent:
        @RenderSubAgentBlock()
        break;
    
    case ContentBlockType.Permission:
        @RenderPermissionBlock()
        break;
    
    case ContentBlockType.Divider:
        @RenderDividerBlock()
        break;
    
    default:
        <div class="content-unknown">@Block.Content</div>
        break;
}

@code {
    // 新增渲染方法
    
    private RenderFragment RenderErrorBlock() => builder =>
    {
        builder.OpenComponent<Alert>(0);
        builder.AddAttribute(1, "Type", AlertType.Error);
        builder.AddAttribute(2, "Message", Block.Content);
        builder.AddAttribute(3, "ShowIcon", true);
        builder.CloseComponent();
    };
    
    private RenderFragment RenderSubAgentBlock() => builder =>
    {
        var agentName = Block.Extensions?.GetValueOrDefault("subAgentName")?.ToString() ?? "SubAgent";
        
        builder.OpenElement(0, "div");
        builder.AddAttribute(1, "class", "subagent-block");
        
        builder.OpenElement(2, "div");
        builder.AddAttribute(3, "class", "subagent-header");
        builder.AddContent(4, $"🤖 {agentName}");
        builder.CloseElement();
        
        if (!string.IsNullOrEmpty(Block.Content))
        {
            builder.OpenElement(5, "div");
            builder.AddAttribute(6, "class", "subagent-content");
            builder.AddContent(7, new MarkupString(RenderMarkdown(Block.Content)));
            builder.CloseElement();
        }
        
        builder.CloseElement();
    };
    
    private RenderFragment RenderPermissionBlock() => builder =>
    {
        if (Block.Extensions?.TryGetValue("permission", out var perm) == true && 
            perm is PermissionRequestViewModel permission)
        {
            builder.OpenComponent<PermissionDialog>(0);
            builder.AddAttribute(1, "Permission", permission);
            builder.AddAttribute(2, "OnDecision", EventCallback.Factory.Create<PermissionDecision>(this, HandlePermissionDecision));
            builder.CloseComponent();
        }
    };
    
    private RenderFragment RenderDividerBlock() => builder =>
    {
        var stepIndex = Block.Extensions?.GetValueOrDefault("stepIndex") as int? ?? 0;
        
        builder.OpenElement(0, "div");
        builder.AddAttribute(1, "class", "step-divider");
        builder.AddAttribute(2, "style", "display: flex; align-items: center; gap: 8px; margin: 8px 0;");
        
        builder.OpenElement(3, "div");
        builder.AddAttribute(4, "class", "step-badge");
        builder.AddContent(5, $"Step {stepIndex + 1}");
        builder.CloseElement();
        
        builder.OpenElement(6, "div");
        builder.AddAttribute(7, "class", "step-line");
        builder.CloseElement();
        
        builder.CloseElement();
    };
    
    private async Task HandlePermissionDecision(PermissionDecision decision)
    {
        // 通过 PermissionChannel 发送响应
        // 实现细节取决于权限系统设计
    }
}
```

---

### 2.2 中优先级问题 (P1)

#### [P1-001] StepStart 与 StreamStart 语义重叠
**问题**: 两个事件职责重叠，同时发射造成冗余

**解决方案**: 明确职责分工
```csharp
// 文件: src/Seeing.Agent/Core/AgentExecutor.cs

// 方案：保留两者，但明确职责差异
// - StepStart: Step 生命周期管理，包含 Step 类型和描述
// - StreamStart: 流式渲染信号，向后兼容

// 在文档中明确说明：
/// <summary>
/// Step 开始事件 - 标记一个处理步骤开始
/// </summary>
/// <remarks>
/// 职责差异：
/// - StepStart: 用于 Step 生命周期管理，包含 StepType (thinking/tool_calling/responding)
/// - StreamStart: 用于流式渲染开始信号，向后兼容现有消费者
/// 
/// 时序关系：
/// StepStart → StreamStart → StreamDelta* → StreamComplete → StepComplete
/// </remarks>
public record StepStartEvent : IMessageEvent { ... }
```

#### [P1-002] SubAgentEventForward 缺少深度限制
**问题**: 无限嵌套可能导致事件风暴

**解决方案**: 添加深度限制配置
```csharp
// 文件: src/Seeing.Agent/Configuration/SeeingAgentOptions.cs

public class SeeingAgentOptions
{
    // 现有配置...
    
    /// <summary>
    /// 子代理最大嵌套深度（默认 3）
    /// </summary>
    public int MaxSubAgentDepth { get; set; } = 3;
}

// 文件: src/Seeing.Agent/Core/AgentExecutor.cs

private async IAsyncEnumerable<IMessageEvent> ForwardSubAgentEventsAsync(
    IAsyncEnumerable<IMessageEvent> subEvents,
    int currentDepth,
    string subAgentName,
    string subSessionId,
    string loopId,
    [EnumeratorCancellation] CancellationToken cancellationToken)
{
    if (currentDepth >= _options.MaxSubAgentDepth)
    {
        _logger.LogWarning("[AgentExecutor] 达到子代理最大深度 {Depth}, 不再转发事件", currentDepth);
        yield break;
    }
    
    await foreach (var evt in subEvents.WithCancellation(cancellationToken))
    {
        yield return new SubAgentEventForward
        {
            SessionId = evt.SessionId,
            LoopId = loopId,
            SubAgentName = subAgentName,
            SubSessionId = subSessionId,
            ForwardedEvent = evt,
            Depth = currentDepth + 1
        };
    }
}
```

#### [P1-003] AgentExecutor 取消检查点不足
**问题**: LLM 流式调用期间无法响应取消

**解决方案**: 在流式循环中添加取消检查
```csharp
// 文件: src/Seeing.Agent/Core/AgentExecutor.cs

await foreach (var update in _llm.CompleteStreamAsync(
    ResolveModelId(agent),
    request,
    context.SessionId,
    effectiveToken))
{
    // 添加取消检查（每 10 次迭代检查一次）
    if (++iterationCount % 10 == 0 && effectiveToken.IsCancellationRequested)
    {
        // 发射取消事件
        yield return new LoopCancelledEvent
        {
            SessionId = context.SessionId,
            LoopId = loopId,
            Reason = "user",
            CompletedSteps = totalSteps,
            PartialMessages = completedMessages,
            PartialUsage = totalUsage
        };
        loopCompleteEmitted = true;
        yield break;
    }
    
    // 处理增量...
}
```

#### [P1-004] EventStreamHandler 缺少 default 分支
**问题**: 新事件类型会被静默忽略

**解决方案**: 添加 default 分支
```csharp
// 文件: samples/Seeing.Agent.WebUI/Services/EventStreamHandler.cs

public async Task ProcessEventAsync(IMessageEvent evt)
{
    switch (evt.Type)
    {
        // 现有 case 分支...
        
        default:
            // 记录未处理事件（用于调试和监控）
            _logger.LogDebug("[EventStreamHandler] 未处理事件类型: {EventType}, EventId: {EventId}", 
                evt.Type, evt.GetType().Name);
            break;
    }
    
    OnStateChanged?.Invoke();
}
```

#### [P1-005] ErrorMessageStrategy 判断条件需要完善
**问题**: 现有错误消息可能无法被正确匹配

**解决方案**: 完善判断条件
```csharp
// 文件: samples/Seeing.Agent.WebUI/Components/Messaging/MessageRenderStrategies.cs

public class ErrorMessageStrategy : IMessageRenderStrategy
{
    public string Name => "Error";
    
    public bool CanHandle(MessageViewModel message) =>
        // 1. 显式的 error 角色
        message.Role.Equals("error", StringComparison.OrdinalIgnoreCase) ||
        // 2. 内容包含错误标记
        message.Content?.Contains("⚠️ 错误:") == true ||
        message.Content?.Contains("❌") == true ||
        // 3. 工具调用失败
        (message.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) &&
         message.ToolCalls.Any(t => t.Status == "failed" && !string.IsNullOrEmpty(t.Error)));
    
    // 其他方法...
}
```

---

### 2.3 低优先级问题 (P2)

#### [P2-001] BuildLoopFromMessage 重复计算
**解决方案**: 删除冗余逻辑
```csharp
// 文件: samples/Seeing.Agent.WebUI/Components/MessageList.razor
// 删除第 142-165 行的重复 loopIndex 计算逻辑
// 只保留使用 _loopIndexMap 的实现
```

#### [P2-002] ToolMessageStrategy 未使用
**解决方案**: 在 MessageList 中添加 tool 消息分支
```razor
<!-- MessageList.razor -->
else if (message.Role == "tool")
{
    <ChatMessageItem @key="message.Id"
                     Message="@message" 
                     OnToolClick="@OnToolClickHandler" />
}
```

---

## 三、新增建议实现

### 3.1 事件序列号（建议 S-002）
```csharp
// 文件: src/Seeing.Agent/Core/Events/MessageEventTypes.cs

public interface IMessageEvent
{
    string SessionId { get; }
    string? LoopId { get; }
    DateTime Timestamp { get; }
    MessageEventType Type { get; }
    
    /// <summary>
    /// 事件序列号（全局递增，用于调试时序问题）
    /// </summary>
    long SequenceNumber { get; }
}

// 实现自动递增
public static class EventSequence
{
    private static long _current = 0;
    
    public static long Next() => Interlocked.Increment(ref _current);
}

// 每个事件使用
public record LoopStartEvent : IMessageEvent
{
    public long SequenceNumber { get; init; } = EventSequence.Next();
    // ...
}
```

### 3.2 权限审计字段（建议 S-004）
```csharp
public record PermissionResponseEvent : IMessageEvent
{
    // 现有字段...
    
    /// <summary>决策用户 ID</summary>
    public string? UserId { get; init; }
    
    /// <summary>客户端 IP</summary>
    public string? ClientIp { get; init; }
    
    /// <summary>响应耗时</summary>
    public TimeSpan ResponseTime { get; init; }
    
    /// <summary>会话 ID（用于审计追溯）</summary>
    public string? AuditSessionId { get; init; }
}
```

### 3.3 内容块值相等比较（建议 S-003）
```csharp
// 文件: samples/Seeing.Agent.WebUI/Models/Messaging/ContentBlock.cs

public class ContentBlock : IEquatable<ContentBlock>
{
    // 现有成员...
    
    public bool Equals(ContentBlock? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        
        return Id == other.Id &&
               Type == other.Type &&
               Content == other.Content &&
               IsComplete == other.IsComplete &&
               IsStreaming == other.IsStreaming &&
               Equals(ToolCall, other.ToolCall);
    }
    
    public override int GetHashCode()
    {
        return HashCode.Combine(Id, Type, Content, IsComplete);
    }
}
```

---

## 四、实施计划（修订版）

| 阶段 | 内容 | 优先级 | 预计工时 |
|-----|------|-------|---------|
| **Phase 1** | P0-001: 统一 PermissionRequestEvent | P0 | 1h |
| **Phase 2** | P0-002: 重构 Loop 分组缓存 | P0 | 2h |
| **Phase 3** | P0-003: 确定性 ContentBlock ID | P0 | 1h |
| **Phase 4** | P0-004: 扩展 ContentBlockRenderer | P0 | 2h |
| **Phase 5** | P1-001~005: 中优先级问题修复 | P1 | 3h |
| **Phase 6** | P2-001~002: 低优先级问题修复 | P2 | 1h |
| **Phase 7** | S-001~004: 建议实现（可选） | P3 | 2h |
| **Phase 8** | 测试 + 文档更新 | - | 2h |
| **总计** | | | **14h** |

---

## 五、风险评估（修订版）

| 风险 | 原评估 | 修订评估 | 缓解措施 |
|-----|-------|---------|---------|
| 事件类型膨胀 | 中 | 低 | 已明确职责分工，按领域分组 |
| 缓存一致性 | 高 | 中 | 增量更新 + 指数索引，避免全量重建 |
| 向后兼容 | 中 | 低 | 保留旧事件，新增事件可选处理 |
| 性能回退 | 高 | 低 | 确定性 ID + 增量更新，性能有保障 |
| 内存泄漏 | 未评估 | 中 | 已添加 MaxCacheSize 限制 |

---

## 六、验收标准

### 事件推送验收
- [ ] LoopCancelled 事件在取消时正确发射
- [ ] StepStart/StepComplete 正确包裹每个 Step
- [ ] PermissionRequest/Response 正确处理权限交互
- [ ] 所有错误路径都发射 LoopComplete 或 LoopCancelled
- [ ] 事件序列号正确递增

### 渲染架构验收
- [ ] Loop 分组渲染性能提升 > 80%（缓存命中时）
- [ ] 内容块增量更新正确工作（确定性 ID 匹配）
- [ ] Error/SubAgent/Permission/Divider 类型正确渲染
- [ ] 缓存内存使用稳定（不无限增长）
- [ ] 取消场景 UI 正确显示"已取消"状态

---

## 七、附录：审查意见汇总

### 事件推送审查意见 (Oracle #1)

**通过项**: 8 项
**问题项**: 6 项 (P-001 ~ P-006)
**建议项**: 6 项 (S-001 ~ S-006)
**总分**: 4/5

### 渲染架构审查意见 (Oracle #2)

**通过项**: 10 项
**问题项**: 8 项 (P-001 ~ P-008)
**建议项**: 6 项 (S-001 ~ S-006)
**总分**: 3.2/5

### 核心问题总结

1. **PermissionRequestEvent 定义冲突** - 需统一定义
2. **缓存方案根本缺陷** - 需重新设计
3. **增量更新无法工作** - 需确定性 ID
4. **新类型渲染缺失** - 需扩展渲染器

修复以上问题后，整体方案评分预计可提升至 **4.5/5**。
