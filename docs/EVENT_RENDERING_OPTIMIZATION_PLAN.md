# AgentLoop 事件推送与渲染架构优化方案

> 版本: 1.0  
> 日期: 2025-01-15  
> 状态: 待审查

---

## 一、问题诊断

### 1.1 事件推送问题

| 问题ID | 问题描述 | 严重程度 | 影响范围 |
|--------|---------|---------|---------|
| E-001 | 缺少 `LoopCancelled` 事件，用户取消时无明确标记 | 高 | 用户体验 |
| E-002 | `LoopComplete` 在错误路径可能重复发射 | 中 | 数据一致性 |
| E-003 | 子代理内部事件未向上传递，嵌套场景不可见 | 中 | 可观测性 |
| E-004 | 缺少 `PermissionRequest/Response` 事件，权限交互需特殊处理 | 中 | 架构一致性 |
| E-005 | 缺少 `StepStart/StepComplete` 事件，Step 边界语义不明确 | 低 | 语义清晰度 |

### 1.2 渲染架构问题

| 问题ID | 问题描述 | 严重程度 | 影响范围 |
|--------|---------|---------|---------|
| R-001 | `BuildLoopFromMessage` 每次渲染 O(n) 遍历，无缓存 | 高 | 渲染性能 |
| R-002 | 缺少 `ErrorMessageStrategy`，错误消息无专门渲染策略 | 中 | 用户体验 |
| R-003 | `ToolMessageStrategy` 存在但未实际使用 | 低 | 代码冗余 |
| R-004 | 内容块重建频繁，流式更新时每次都重新构建 | 中 | 渲染性能 |
| R-005 | 子代理消息不独立渲染，嵌套场景信息丢失 | 中 | 可观测性 |

---

## 二、事件推送优化方案

### 2.1 新增事件类型

```csharp
// 文件: src/Seeing.Agent/Core/Events/MessageEventTypes.cs

public enum MessageEventType
{
    // === Loop 级事件 ===
    LoopStart,           // ✅ 已有
    LoopComplete,        // ✅ 已有
    LoopCancelled,       // 🆕 新增：Loop 被取消
    
    // === Step 级事件 ===
    StepStart,           // 🆕 新增：Step 开始（语义更明确）
    StepComplete,        // 🆕 新增：Step 结束
    StreamStart,         // ✅ 已有（保留向后兼容）
    StreamDelta,         // ✅ 已有
    StreamComplete,      // ✅ 已有（保留向后兼容）
    
    // === Tool 级事件 ===
    ToolCallPending,     // ✅ 已有
    ToolCallRunning,     // ✅ 已有
    ToolCallComplete,    // ✅ 已有
    
    // === SubAgent 级事件 ===
    SubAgentStarted,     // ✅ 已有
    SubAgentCompleted,   // ✅ 已有
    SubAgentEventForward, // 🆕 新增：子代理事件转发
    
    // === Permission 级事件 ===
    PermissionRequest,   // 🆕 新增：权限请求
    PermissionResponse,  // 🆕 新增：权限响应
    
    // === 其他 ===
    Error                // ✅ 已有
}
```

### 2.2 新增事件定义

```csharp
/// <summary>
/// Loop 被取消事件 - 用户主动取消或超时
/// </summary>
public record LoopCancelledEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public required string LoopId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public MessageEventType Type => MessageEventType.LoopCancelled;
    
    /// <summary>取消原因: user, timeout, error, resource_limit</summary>
    public required string Reason { get; init; }
    
    /// <summary>已完成的步数</summary>
    public int CompletedSteps { get; init; }
    
    /// <summary>已完成的消息（部分结果）</summary>
    public List<ChatMessage>? PartialMessages { get; init; }
    
    /// <summary>Token 使用统计（部分）</summary>
    public TokenUsage? PartialUsage { get; init; }
}

/// <summary>
/// Step 开始事件 - 标记一个处理步骤开始
/// </summary>
public record StepStartEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public string? LoopId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public MessageEventType Type => MessageEventType.StepStart;
    
    /// <summary>步骤索引 (0, 1, 2...)</summary>
    public int Step { get; init; }
    
    /// <summary>步骤类型: thinking, tool_calling, responding</summary>
    public string StepType { get; init; } = "thinking";
    
    /// <summary>步骤描述（可选）</summary>
    public string? Description { get; init; }
}

/// <summary>
/// Step 结束事件 - 标记一个处理步骤结束
/// </summary>
public record StepCompleteEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public string? LoopId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public MessageEventType Type => MessageEventType.StepComplete;
    
    /// <summary>步骤索引</summary>
    public int Step { get; init; }
    
    /// <summary>是否成功</summary>
    public bool Success { get; init; }
    
    /// <summary>步骤耗时</summary>
    public TimeSpan Duration { get; init; }
    
    /// <summary>产生的工具调用数量</summary>
    public int ToolCallsCount { get; init; }
}

/// <summary>
/// 权限请求事件 - 需要用户确认
/// </summary>
public record PermissionRequestEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public string? LoopId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public MessageEventType Type => MessageEventType.PermissionRequest;
    
    /// <summary>权限请求 ID</summary>
    public required string PermissionId { get; init; }
    
    /// <summary>权限类型: tool, file, network, shell</summary>
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

/// <summary>
/// 权限响应事件 - 用户确认/拒绝
/// </summary>
public record PermissionResponseEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public string? LoopId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public MessageEventType Type => MessageEventType.PermissionResponse;
    
    /// <summary>对应的权限请求 ID</summary>
    public required string PermissionId { get; init; }
    
    /// <summary>用户决策: allow, deny, ask</summary>
    public required string Decision { get; init; }
    
    /// <summary>决策原因（可选）</summary>
    public string? Reason { get; init; }
    
    /// <summary>是否记住决策（会话级别）</summary>
    public bool Remember { get; init; }
}

/// <summary>
/// 子代理事件转发 - 将子代理内部事件向上传递
/// </summary>
public record SubAgentEventForward : IMessageEvent
{
    public required string SessionId { get; init; }
    public string? LoopId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public MessageEventType Type => MessageEventType.SubAgentEventForward;
    
    /// <summary>子代理名称</summary>
    public required string SubAgentName { get; init; }
    
    /// <summary>子会话 ID</summary>
    public required string SubSessionId { get; init; }
    
    /// <summary>转发的事件</summary>
    public required IMessageEvent ForwardedEvent { get; init; }
    
    /// <summary>嵌套深度</summary>
    public int Depth { get; init; }
}
```

### 2.3 AgentExecutor 重构方案

```csharp
// 文件: src/Seeing.Agent/Core/AgentExecutor.cs

public async IAsyncEnumerable<IMessageEvent> ExecuteAsync(
    AgentDefinition agent,
    AgentContext context,
    [EnumeratorCancellation] CancellationToken cancellationToken = default)
{
    // ========== 初始化 ==========
    var loopId = Guid.NewGuid().ToString("N");
    var loopStartTime = DateTime.Now;
    var totalSteps = 0;
    TokenUsage? totalUsage = null;
    var completedMessages = new List<ChatMessage>();
    var loopCompleteEmitted = false; // 防止重复发射
    
    // ========== 发布 LoopStart ==========
    yield return new LoopStartEvent
    {
        SessionId = context.SessionId,
        LoopId = loopId,
        UserInput = context.History.LastOrDefault()?.Content
    };
    
    try
    {
        var maxSteps = agent.MaxSteps ?? 32;
        var messages = context.History.ToList();
        
        for (var step = 0; step < maxSteps; step++)
        {
            // 检查取消
            if (cancellationToken.IsCancellationRequested)
            {
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
            
            totalSteps = step + 1;
            var stepStartTime = DateTime.Now;
            
            // ========== 发布 StepStart ==========
            yield return new StepStartEvent
            {
                SessionId = context.SessionId,
                LoopId = loopId,
                Step = step,
                StepType = "thinking"
            };
            
            // ========== 发布 StreamStart（向后兼容）==========
            yield return new StreamStartEvent
            {
                SessionId = context.SessionId,
                LoopId = loopId,
                Step = step
            };
            
            // ========== LLM 调用 ==========
            ChatMessage? assistantMessage = null;
            var streamingContent = new StringBuilder();
            var streamingReasoning = new StringBuilder();
            
            await foreach (var update in _llm.CompleteStreamAsync(...))
            {
                // 处理增量...
                if (!string.IsNullOrEmpty(update.ReasoningDelta))
                {
                    streamingReasoning.Append(update.ReasoningDelta);
                    yield return new StreamDeltaEvent
                    {
                        SessionId = context.SessionId,
                        LoopId = loopId,
                        ReasoningDelta = update.ReasoningDelta
                    };
                }
                
                if (!string.IsNullOrEmpty(update.ContentDelta))
                {
                    streamingContent.Append(update.ContentDelta);
                    yield return new StreamDeltaEvent
                    {
                        SessionId = context.SessionId,
                        LoopId = loopId,
                        ContentDelta = update.ContentDelta
                    };
                }
                
                // 累加 Usage...
            }
            
            // ========== 发布 StreamComplete ==========
            if (assistantMessage == null)
            {
                yield return new ErrorEvent
                {
                    SessionId = context.SessionId,
                    LoopId = loopId,
                    Message = "LLM 返回空响应",
                    Source = "llm"
                };
                
                yield return new StepCompleteEvent
                {
                    SessionId = context.SessionId,
                    LoopId = loopId,
                    Step = step,
                    Success = false,
                    Duration = DateTime.Now - stepStartTime
                };
                
                continue; // 继续下一步，或由外部决定终止
            }
            
            messages.Add(assistantMessage);
            completedMessages.Add(assistantMessage);
            
            yield return new StreamCompleteEvent
            {
                SessionId = context.SessionId,
                LoopId = loopId,
                Message = assistantMessage,
                Usage = lastUsage
            };
            
            // ========== 工具调用 ==========
            var toolCallsCount = 0;
            if (assistantMessage.ToolCalls?.Count > 0)
            {
                await foreach (var toolEvent in ExecuteToolCallsAsync(...))
                {
                    yield return toolEvent;
                    
                    if (toolEvent is ToolCallEvent { Status: ToolCallStatus.Success or ToolCallStatus.Failed })
                    {
                        toolCallsCount++;
                    }
                }
            }
            
            // ========== 发布 StepComplete ==========
            yield return new StepCompleteEvent
            {
                SessionId = context.SessionId,
                LoopId = loopId,
                Step = step,
                Success = true,
                Duration = DateTime.Now - stepStartTime,
                ToolCallsCount = toolCallsCount
            };
            
            // 无工具调用则结束
            if (assistantMessage.ToolCalls == null || assistantMessage.ToolCalls.Count == 0)
            {
                yield return new LoopCompleteEvent
                {
                    SessionId = context.SessionId,
                    LoopId = loopId,
                    TotalSteps = totalSteps,
                    Duration = DateTime.Now - loopStartTime,
                    Success = true,
                    Usage = totalUsage
                };
                loopCompleteEmitted = true;
                yield break;
            }
        }
        
        // 达到最大步数
        yield return new ErrorEvent
        {
            SessionId = context.SessionId,
            LoopId = loopId,
            Message = $"达到最大步数 {maxSteps}",
            Source = "agent"
        };
        
        yield return new LoopCompleteEvent
        {
            SessionId = context.SessionId,
            LoopId = loopId,
            TotalSteps = totalSteps,
            Duration = DateTime.Now - loopStartTime,
            Success = false,
            Error = $"达到最大步数 {maxSteps}",
            Usage = totalUsage
        };
        loopCompleteEmitted = true;
    }
    catch (OperationCanceledException)
    {
        // 用户取消
        if (!loopCompleteEmitted)
        {
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
        }
    }
    catch (Exception ex)
    {
        // 异常终止
        if (!loopCompleteEmitted)
        {
            yield return new ErrorEvent
            {
                SessionId = context.SessionId,
                LoopId = loopId,
                Message = ex.Message,
                Exception = ex,
                Source = "agent"
            };
            
            yield return new LoopCompleteEvent
            {
                SessionId = context.SessionId,
                LoopId = loopId,
                TotalSteps = totalSteps,
                Duration = DateTime.Now - loopStartTime,
                Success = false,
                Error = ex.Message,
                Usage = totalUsage
            };
            loopCompleteEmitted = true;
        }
    }
}
```

### 2.4 权限请求处理方案

```csharp
// 文件: src/Seeing.Agent/Core/AgentExecutor.cs (工具执行部分)

private async IAsyncEnumerable<IMessageEvent> ExecuteToolCallsAsync(...)
{
    foreach (var tc in toolCalls)
    {
        var name = tc.Function?.Name ?? "";
        
        // ========== 权限评估 ==========
        var decision = await EvaluatePermissionAsync(name, arguments, ...);
        
        if (decision.Action == PermissionAction.Ask)
        {
            // 🆕 发布权限请求事件
            var permissionId = Guid.NewGuid().ToString("N");
            yield return new PermissionRequestEvent
            {
                SessionId = context.SessionId,
                LoopId = loopId,
                PermissionId = permissionId,
                PermissionKind = "tool",
                Resource = name,
                Arguments = arguments,
                RiskLevel = DetermineRiskLevel(name, arguments),
                Message = $"工具 '{name}' 需要您的授权"
            };
            
            // 等待用户响应
            var response = await permissionChannel.WaitForResponseAsync(permissionId);
            
            // 🆕 发布权限响应事件
            yield return new PermissionResponseEvent
            {
                SessionId = context.SessionId,
                LoopId = loopId,
                PermissionId = permissionId,
                Decision = response.Action.ToString().ToLowerInvariant(),
                Reason = response.Reason
            };
            
            if (response.Action != PermissionAction.Allow)
            {
                // 拒绝
                yield return new ToolCallEvent
                {
                    SessionId = context.SessionId,
                    LoopId = loopId,
                    Type = MessageEventType.ToolCallComplete,
                    ToolCallId = tc.Id,
                    ToolName = name,
                    Status = ToolCallStatus.Rejected,
                    Error = "用户拒绝"
                };
                continue;
            }
        }
        
        // 执行工具...
    }
}
```

---

## 三、渲染架构优化方案

### 3.1 Loop 分组缓存方案

```csharp
// 文件: samples/Seeing.Agent.WebUI/Components/MessageList.razor

@code {
    // ========== Loop 缓存系统 ==========
    private Dictionary<string, LoopGroupViewModel> _loopCache = new();
    private Dictionary<string, List<MessageViewModel>> _loopMessages = new();
    private int _lastMessageCount = 0;
    private string _lastMessageFingerprint = "";
    
    /// <summary>
    /// 获取或构建 Loop 分组（带缓存）
    /// </summary>
    private LoopGroupViewModel GetOrBuildLoop(MessageViewModel message)
    {
        // 检查是否需要刷新缓存
        if (NeedsCacheRefresh())
        {
            RefreshLoopCache();
        }
        
        var loopId = message.LoopId ?? $"single-{Messages.IndexOf(message)}";
        
        if (_loopCache.TryGetValue(loopId, out var cached))
        {
            return cached;
        }
        
        // 构建新的 Loop
        var loop = BuildLoopInternal(loopId, message);
        _loopCache[loopId] = loop;
        return loop;
    }
    
    /// <summary>
    /// 判断是否需要刷新缓存
    /// </summary>
    private bool NeedsCacheRefresh()
    {
        if (Messages.Count != _lastMessageCount)
            return true;
        
        // 计算指纹（只计算 LoopId 和 IsComplete）
        var fingerprint = string.Join("|", Messages
            .Where(m => m.Role == "assistant")
            .Select(m => $"{m.LoopId}:{m.IsComplete}"));
        
        if (fingerprint != _lastMessageFingerprint)
        {
            _lastMessageFingerprint = fingerprint;
            return true;
        }
        
        return false;
    }
    
    /// <summary>
    /// 刷新 Loop 缓存
    /// </summary>
    private void RefreshLoopCache()
    {
        _lastMessageCount = Messages.Count;
        
        // 重新分组
        _loopMessages.Clear();
        
        foreach (var msg in Messages.Where(m => m.Role == "assistant"))
        {
            var loopId = msg.LoopId ?? $"single-{Messages.IndexOf(msg)}";
            if (!_loopMessages.ContainsKey(loopId))
            {
                _loopMessages[loopId] = new List<MessageViewModel>();
            }
            _loopMessages[loopId].Add(msg);
        }
        
        // 重建缓存
        var newCache = new Dictionary<string, LoopGroupViewModel>();
        var loopIndex = 0;
        
        foreach (var kvp in _loopMessages)
        {
            var loopId = kvp.Key;
            var loopMessages = kvp.Value;
            
            // 检查是否可以复用旧缓存
            if (_loopCache.TryGetValue(loopId, out var cached) &&
                cached.Messages.Count == loopMessages.Count &&
                cached.Messages.All(m => loopMessages.Any(lm => lm.Id == m.Id && lm.IsComplete == m.IsComplete)))
            {
                // 更新状态但不重建
                cached.IsComplete = loopMessages.All(m => m.IsComplete);
                newCache[loopId] = cached;
            }
            else
            {
                // 构建新的
                newCache[loopId] = BuildLoopViewModel(loopId, loopMessages, ++loopIndex);
            }
        }
        
        _loopCache = newCache;
    }
}
```

### 3.2 内容块增量更新方案

```csharp
// 文件: samples/Seeing.Agent.WebUI/Models/Messaging/ContentBlock.cs

/// <summary>
/// 内容块差异计算器 - 支持增量更新
/// </summary>
public class ContentBlockDiffCalculator
{
    /// <summary>
    /// 计算内容块差异
    /// </summary>
    public static ContentBlockDiff CalculateDiff(
        List<ContentBlock> oldBlocks,
        List<ContentBlock> newBlocks)
    {
        var diff = new ContentBlockDiff();
        
        var oldDict = oldBlocks.ToDictionary(b => b.Id);
        var newDict = newBlocks.ToDictionary(b => b.Id);
        
        // 新增的块
        diff.Added = newBlocks
            .Where(b => !oldDict.ContainsKey(b.Id))
            .ToList();
        
        // 删除的块
        diff.Removed = oldBlocks
            .Where(b => !newDict.ContainsKey(b.Id))
            .ToList();
        
        // 更新的块（内容或状态变化）
        diff.Updated = newBlocks
            .Where(b => oldDict.TryGetValue(b.Id, out var old) && 
                       (old.Content != b.Content || 
                        old.IsComplete != b.IsComplete ||
                        old.IsStreaming != b.IsStreaming))
            .ToList();
        
        // 未变化的块
        diff.Unchanged = newBlocks
            .Where(b => oldDict.TryGetValue(b.Id, out var old) &&
                       old.Content == b.Content &&
                       old.IsComplete == b.IsComplete)
            .ToList();
        
        return diff;
    }
}

/// <summary>
/// 内容块差异
/// </summary>
public class ContentBlockDiff
{
    public List<ContentBlock> Added { get; set; } = new();
    public List<ContentBlock> Removed { get; set; } = new();
    public List<ContentBlock> Updated { get; set; } = new();
    public List<ContentBlock> Unchanged { get; set; } = new();
    
    public bool HasChanges => Added.Count > 0 || Removed.Count > 0 || Updated.Count > 0;
}
```

### 3.3 新增渲染策略

```csharp
// 文件: samples/Seeing.Agent.WebUI/Components/Messaging/MessageRenderStrategies.cs

/// <summary>
/// 错误消息渲染策略
/// </summary>
public class ErrorMessageStrategy : IMessageRenderStrategy
{
    public string Name => "Error";
    
    public bool CanHandle(MessageViewModel message) =>
        message.Role.Equals("error", StringComparison.OrdinalIgnoreCase) ||
        (message.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) &&
         message.ToolCalls.Any(t => !string.IsNullOrEmpty(t.Error) && t.Status == "failed"));
    
    public string GetMessageClass(MessageViewModel message) =>
        "message-item message-error";
    
    public bool ShouldShowReasoning(MessageViewModel message) => false;
    public bool ShouldShowAttachments(MessageViewModel message) => false;
    public bool ShouldShowToolCalls(MessageViewModel message) => true;
    public bool ShouldShowLoading(MessageViewModel message) => false;
}

/// <summary>
/// 子代理消息渲染策略
/// </summary>
public class SubAgentMessageStrategy : IMessageRenderStrategy
{
    public string Name => "SubAgent";
    
    public bool CanHandle(MessageViewModel message) =>
        message.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) &&
        message.Extensions?.ContainsKey("subAgent") == true;
    
    public string GetMessageClass(MessageViewModel message)
    {
        var subAgentName = message.Extensions?.GetValueOrDefault("subAgent")?.ToString() ?? "unknown";
        return $"message-item message-assistant message-subagent subagent-{subAgentName.ToLowerInvariant()}";
    }
    
    public bool ShouldShowReasoning(MessageViewModel message) => 
        !string.IsNullOrEmpty(message.Reasoning);
    public bool ShouldShowAttachments(MessageViewModel message) => false;
    public bool ShouldShowToolCalls(MessageViewModel message) => message.ToolCalls.Count > 0;
    public bool ShouldShowLoading(MessageViewModel message) => !message.IsComplete;
}

/// <summary>
/// 权限请求消息渲染策略
/// </summary>
public class PermissionRequestStrategy : IMessageRenderStrategy
{
    public string Name => "PermissionRequest";
    
    public bool CanHandle(MessageViewModel message) =>
        message.Role.Equals("system", StringComparison.OrdinalIgnoreCase) &&
        message.Extensions?.ContainsKey("permissionRequest") == true;
    
    public string GetMessageClass(MessageViewModel message) =>
        "message-item message-permission-request";
    
    public bool ShouldShowReasoning(MessageViewModel message) => false;
    public bool ShouldShowAttachments(MessageViewModel message) => false;
    public bool ShouldShowToolCalls(MessageViewModel message) => false;
    public bool ShouldShowLoading(MessageViewModel message) => false;
}
```

### 3.4 新增内容块类型

```csharp
// 文件: samples/Seeing.Agent.WebUI/Models/Messaging/ContentBlock.cs

public enum ContentBlockType
{
    Reasoning,    // ✅ 已有
    Text,         // ✅ 已有
    ToolCall,     // ✅ 已有
    Attachment,   // ✅ 已有
    Image,        // ✅ 已有
    
    // 🆕 新增类型
    Error,        // 错误信息块
    SubAgent,     // 子代理块
    Permission,   // 权限请求块
    Progress,     // 进度条块
    Divider       // 分隔线（Step 之间）
}

/// <summary>
/// 扩展工厂方法
/// </summary>
public partial class ContentBlock
{
    public static ContentBlock CreateError(string error, int sortIndex)
    {
        return new ContentBlock
        {
            Type = ContentBlockType.Error,
            Content = error,
            SortIndex = sortIndex,
            IsComplete = true
        };
    }
    
    public static ContentBlock CreateSubAgent(
        string agentName,
        string? content,
        int sortIndex,
        bool isComplete = true)
    {
        return new ContentBlock
        {
            Type = ContentBlockType.SubAgent,
            Content = content,
            SortIndex = sortIndex,
            IsComplete = isComplete,
            Extensions = new Dictionary<string, object>
            {
                ["subAgentName"] = agentName
            }
        };
    }
    
    public static ContentBlock CreatePermission(
        PermissionRequestViewModel permission,
        int sortIndex)
    {
        return new ContentBlock
        {
            Type = ContentBlockType.Permission,
            SortIndex = sortIndex,
            IsComplete = false,
            Extensions = new Dictionary<string, object>
            {
                ["permission"] = permission
            }
        };
    }
    
    public static ContentBlock CreateDivider(int stepIndex, int sortIndex)
    {
        return new ContentBlock
        {
            Type = ContentBlockType.Divider,
            SortIndex = sortIndex,
            IsComplete = true,
            Extensions = new Dictionary<string, object>
            {
                ["stepIndex"] = stepIndex
            }
        };
    }
}
```

### 3.5 EventStreamHandler 扩展

```csharp
// 文件: samples/Seeing.Agent.WebUI/Services/EventStreamHandler.cs

public async Task ProcessEventAsync(IMessageEvent evt)
{
    switch (evt.Type)
    {
        // === 新增事件处理 ===
        
        case MessageEventType.LoopCancelled:
            HandleLoopCancelled((LoopCancelledEvent)evt);
            break;
        
        case MessageEventType.StepStart:
            HandleStepStart((StepStartEvent)evt);
            break;
        
        case MessageEventType.StepComplete:
            HandleStepComplete((StepCompleteEvent)evt);
            break;
        
        case MessageEventType.PermissionRequest:
            HandlePermissionRequest((PermissionRequestEvent)evt);
            break;
        
        case MessageEventType.PermissionResponse:
            HandlePermissionResponse((PermissionResponseEvent)evt);
            break;
        
        case MessageEventType.SubAgentEventForward:
            HandleSubAgentEventForward((SubAgentEventForward)evt);
            break;
        
        // === 已有事件处理 ===
        case MessageEventType.LoopStart:
            HandleLoopStart((LoopStartEvent)evt);
            break;
        // ... 其他已有事件 ...
    }
    
    OnStateChanged?.Invoke();
}

/// <summary>
/// 处理 Loop 取消事件
/// </summary>
private void HandleLoopCancelled(LoopCancelledEvent evt)
{
    if (_currentLoop != null)
    {
        _currentLoop.EndTime = evt.Timestamp;
        _currentLoop.IsComplete = true;
        _currentLoop.Success = false;
        _currentLoop.Error = $"已取消: {evt.Reason}";
        
        OnLoopComplete?.Invoke(_currentLoop);
    }
    
    // 添加取消标记消息
    if (_sessionState.CurrentSession != null)
    {
        _sessionState.CurrentSession.AddMessage(
            SessionMessage.SystemMessage($"⚠️ 对话已取消: {evt.Reason}"));
    }
}

/// <summary>
/// 处理 Step 开始事件
/// </summary>
private void HandleStepStart(StepStartEvent evt)
{
    // 更新当前 Step
    _currentStep = evt.Step;
    
    // 可选：添加 Step 分隔标记
    if (evt.Step > 0 && _sessionState.CurrentSession != null)
    {
        // 不添加消息，仅更新状态
        // UI 可以通过 Step 变化显示分隔线
    }
}

/// <summary>
/// 处理权限请求事件
/// </summary>
private void HandlePermissionRequest(PermissionRequestEvent evt)
{
    // 添加权限请求到待处理队列
    _pendingPermissions[evt.PermissionId] = new PermissionRequestViewModel
    {
        PermissionId = evt.PermissionId,
        PermissionKind = evt.PermissionKind,
        Resource = evt.Resource,
        Arguments = evt.Arguments,
        RiskLevel = evt.RiskLevel,
        Message = evt.Message,
        TimeoutSeconds = evt.TimeoutSeconds,
        Timestamp = evt.Timestamp
    };
    
    // 触发权限 UI 显示
    OnPermissionRequest?.Invoke(evt);
}

/// <summary>
/// 处理权限响应事件
/// </summary>
private void HandlePermissionResponse(PermissionResponseEvent evt)
{
    // 移除待处理权限
    _pendingPermissions.Remove(evt.PermissionId);
    
    // 触发权限 UI 更新
    OnPermissionResponse?.Invoke(evt);
}
```

---

## 四、向后兼容性保证

### 4.1 事件向后兼容

| 策略 | 说明 |
|-----|------|
| **保留旧事件** | `StreamStart`/`StreamComplete` 保持不变 |
| **新增事件可选** | 新事件不影响现有消费者 |
| **默认处理** | `ProcessEventAsync` 的 switch 添加 default 分支 |

### 4.2 渲染向后兼容

| 策略 | 说明 |
|-----|------|
| **策略顺序** | 新策略插入在 `DefaultMessageStrategy` 之前 |
| **缓存渐进** | 缓存失效时回退到全量构建 |
| **内容块扩展** | 新类型不影响现有类型渲染 |

---

## 五、性能优化预期

| 优化项 | 优化前 | 优化后 | 提升 |
|-------|-------|-------|------|
| Loop 分组计算 | O(n) 每次渲染 | O(1) 缓存命中 | ~90% |
| 内容块构建 | 全量重建 | 增量更新 | ~50% |
| 事件处理 | 无差异计算 | 差异驱动更新 | ~30% |

---

## 六、实施计划

| 阶段 | 内容 | 预计工时 |
|-----|------|---------|
| **Phase 1** | 新增事件类型 + AgentExecutor 重构 | 4h |
| **Phase 2** | EventStreamHandler 扩展 | 2h |
| **Phase 3** | 渲染缓存 + 增量更新 | 3h |
| **Phase 4** | 新增渲染策略 + 内容块类型 | 2h |
| **Phase 5** | 测试 + 文档更新 | 2h |
| **总计** | | **13h** |

---

## 七、风险与缓解

| 风险 | 影响 | 缓解措施 |
|-----|------|---------|
| 事件类型膨胀 | 维护复杂度增加 | 按领域分组，保持单一职责 |
| 缓存一致性 | 状态不同步 | 指纹校验 + 强制刷新机制 |
| 向后兼容 | 现有代码失效 | 保留旧接口 + 渐进迁移 |
| 性能回退 | 缓存失效频繁 | 设置合理的缓存失效阈值 |

