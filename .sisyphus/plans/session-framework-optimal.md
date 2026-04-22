# Session 管理框架最优化设计方案

## TL;DR

> **设计目标**: 设计一个独立、简洁、完备的 Session 管理框架
>
> **核心定位**: Session 是 AI Agent 对话的上下文容器，负责消息历史、状态管理、持久化
>
> **设计原则**: 
> - 职责清晰：Session 管理框架只做 Session 管理，不涉及 Agent 执行逻辑
> - 接口最小：每个接口职责单一，方法数 ≤ 5
> - 可组合：通过组合而非继承扩展功能
> - 可替换：存储、压缩、钩子均可替换

---

## 一、核心概念定义

### 1.1 Session 的本质

**Session 是什么**：
- AI Agent 与用户对话的**上下文容器**
- 包含：消息历史、状态数据、Agent 元信息
- 生命周期：创建 → 使用 → 压缩/清理 → 保存 → 恢复 → 销毁

**Session 不是什么**：
- ❌ 不是 Agent 执行器（Agent 执行是 Agent 包的职责）
- ❌ 不是工具调用器（Tool 执行是 Tool 包的职责）
- ❌ 不是权限管理器（权限是 Rules 包的职责）

### 1.2 Session 管理框架的职责边界

| 职责 | 属于 Session 框架 | 不属于 Session 框架 |
|------|------------------|---------------------|
| 消息历史管理 | ✓ | |
| 会话状态管理 | ✓ | |
| 持久化存储 | ✓ | |
| 会话压缩 | ✓ | |
| 生命周期钩子 | ✓ | |
| Agent 执行 | | ✓ (Agent 包) |
| 工具调用 | | ✓ (Tool 包) |
| 权限控制 | | ✓ (Rules 包) |
| LLM 通信 | | ✓ (Llm 包) |

---

## 二、架构设计

### 2.1 分层架构

```
┌─────────────────────────────────────────────────────────┐
│                    Application Layer                     │
│  (WebUI, CLI, Integration Tests - 使用 SessionManager)  │
└─────────────────────────────────────────────────────────┘
                           │
                           ▼
┌─────────────────────────────────────────────────────────┐
│                    Management Layer                      │
│              SessionManager (协调者)                      │
│  - 创建/销毁 Session                                     │
│  - 协调 Store/Compressor/Hook                            │
│  - 提供 Session 查询接口                                 │
└─────────────────────────────────────────────────────────┘
                           │
           ┌───────────────┼───────────────┐
           ▼               ▼               ▼
┌─────────────┐  ┌─────────────┐  ┌─────────────┐
│ Storage     │  │ Compression │  │ Hooks       │
│ Layer       │  │ Layer       │  │ Layer       │
│ ISession    │  │ ICompress   │  │ ISession    │
│ Store       │  │ Strategy    │  │ Hook        │
└─────────────┘  └─────────────┘  └─────────────┘
           │               │               │
           ▼               ▼               ▼
┌─────────────────────────────────────────────────────────┐
│                      Data Layer                          │
│            SessionData (单一数据模型)                     │
│  - Id, Title, Status, PartitionId                        │
│  - Messages: List<SessionMessage>                        │
│  - Context: Dictionary<string, object>                   │
│  - Agent: AgentMetadata                                  │
└─────────────────────────────────────────────────────────┘
```

### 2.2 核心接口设计（最小化）

#### ISessionStore（存储层）- 5 方法

```csharp
public interface ISessionStore
{
    Task SaveAsync(SessionData data);
    Task<SessionData?> LoadAsync(string id);
    Task DeleteAsync(string id);
    Task<IAsyncEnumerable<SessionData>> ListAsync();
    Task<bool> ExistsAsync(string id);
}
```

**设计理由**：
- 职责单一：只做 CRUD
- 无消息追加方法：消息属于 SessionData，整体保存
- 无批量方法：批量是 ListAsync + SaveAsync 的组合

#### ICompressionStrategy（压缩层）- 3 方法

```csharp
public interface ICompressionStrategy
{
    string Name { get; }
    IReadOnlyList<SessionMessage> Compress(IReadOnlyList<SessionMessage> messages);
    int EstimateRetainedCount(int messageCount);
}
```

**设计理由**：
- 职责单一：只做消息压缩
- 同步方法：压缩是纯计算，无需异步
- 无配置参数：配置通过策略实现类内部处理

#### ISessionHook（钩子层）- 2 方法 + 2 属性

```csharp
public interface ISessionHook
{
    string HookPoint { get; }
    int Priority { get; }
    Task ExecuteAsync(SessionHookContext context);
}

public class SessionHookContext
{
    public string HookPoint { get; init; }
    public string SessionId { get; init; }
    public SessionData? Session { get; init; }
    public IReadOnlyDictionary<string, object> Data { get; init; }
}
```

**设计理由**：
- 职责单一：只响应生命周期事件
- 无返回值控制：Hook 不应阻塞主流程
- Context 包含所有必要信息

#### ISessionManager（管理层）- 5 核心 + 3 扩展方法

```csharp
public interface ISessionManager
{
    // === 核心操作（必须实现）===
    SessionData Create(string? partitionId = null, AgentMetadata? agent = null);
    SessionData? Get(string id);
    bool Delete(string id);
    IReadOnlyList<SessionData> List();
    
    // === 扩展操作（可选实现）===
    Task SaveAsync(string id);       // 持久化
    Task<SessionData?> LoadAsync(string id);  // 从存储恢复
    IReadOnlyList<SessionMessage> Compress(string id);  // 压缩历史
}
```

**设计理由**：
- 核心 5 方法覆盖基本 CRUD
- 扩展方法依赖可选组件（Store/Compressor）
- 返回 SessionData（单一数据模型）

---

## 三、数据模型设计

### 3.1 SessionData（唯一数据模型）

```csharp
/// <summary>
/// Session 数据模型 - 唯一的会话数据结构
/// </summary>
public class SessionData
{
    // === 身份信息 ===
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    
    // === 分区信息 ===
    public string PartitionId { get; set; } = string.Empty;
    
    // === 时间信息 ===
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    
    // === 状态 ===
    public SessionStatus Status { get; set; } = SessionStatus.Created;
    
    // === Agent 信息 ===
    public AgentMetadata? Agent { get; set; }
    
    // === 消息历史 ===
    public List<SessionMessage> Messages { get; set; } = new();
    
    // === 上下文数据 ===
    public Dictionary<string, object> Context { get; set; } = new();
    
    // === 统计信息 ===
    public int MessageCount => Messages.Count;
    
    // === 工厂方法 ===
    public static SessionData Create(string? partitionId = null, AgentMetadata? agent = null)
    {
        var id = GenerateId();
        return new SessionData
        {
            Id = id,
            Title = $"Session {id}",
            PartitionId = partitionId ?? "default",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Agent = agent
        };
    }
    
    private static string GenerateId()
    {
        return $"ses_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}[..8]}";
    }
    
    // === 操作方法 ===
    public void AddMessage(SessionMessage message)
    {
        Messages.Add(message);
        UpdatedAt = DateTime.UtcNow;
    }
    
    public void SetContext(string key, object value)
    {
        Context[key] = value;
        UpdatedAt = DateTime.UtcNow;
    }
    
    public T? GetContext<T>(string key)
    {
        if (Context.TryGetValue(key, out var value) && value is T typed)
            return typed;
        return default;
    }
}
```

**设计理由**：
- 单一模型：无 SessionData/SessionEntry 双重模型
- 自包含：包含所有会话数据（消息、上下文、Agent）
- 可序列化：直接 JSON 序列化
- 线程安全由 SessionManager 保证

### 3.2 SessionStatus（状态枚举）

```csharp
public enum SessionStatus
{
    Created = 0,     // 已创建
    Active = 1,      // 活跃（正在使用）
    Idle = 2,        // 空闲（等待输入）
    Completed = 3,   // 已完成
    Archived = 4,    // 已归档
    Error = 5        // 错误状态
}
```

**设计理由**：
- 语义明确：每个状态有清晰含义
- 最小化：6 个状态覆盖所有场景
- 无 Running：执行状态属于 Agent，不属于 Session

### 3.3 SessionMessage（消息模型）

```csharp
public class SessionMessage
{
    public string? Id { get; set; }
    public string Role { get; set; } = string.Empty;  // system/user/assistant/tool
    public string Content { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    // === 工具调用 ===
    public List<SessionToolCall>? ToolCalls { get; set; }
    public string? ToolCallId { get; set; }
    
    // === 多模态 ===
    public List<SessionContentPart>? Parts { get; set; }
    
    // === 推理 ===
    public string? ReasoningContent { get; set; }
    
    // === 元数据 ===
    public Dictionary<string, object>? Metadata { get; set; }
}
```

**设计理由**：
- 完整支持：文本、多模态、工具调用、推理
- 与 ChatMessage 兼容：可互相转换
- 简化设计：无 Token 统计（属于响应级别）

### 3.4 AgentMetadata（Agent 元信息）

```csharp
public class AgentMetadata
{
    public string AgentId { get; set; } = string.Empty;
    public string AgentName { get; set; } = string.Empty;
    public string Role { get; set; } = "primary";
    public string? ModelId { get; set; }
    public string? ProviderId { get; set; }
}
```

**设计理由**：
- 不依赖 IAgent：Session 包独立
- 最小信息：只保留必要元信息
- 可扩展：通过 Role 区分主/子 Agent

---

## 四、组件设计

### 4.1 SessionManager（核心管理器）

```csharp
public class SessionManager : ISessionManager
{
    // === 内部存储 ===
    private readonly ConcurrentDictionary<string, SessionData> _sessions = new();
    
    // === 可选组件 ===
    private readonly ISessionStore? _store;
    private readonly ICompressionStrategy? _compressor;
    private readonly ISessionHookManager? _hooks;
    private readonly ILogger? _logger;
    
    // === 构造函数（可组合）===
    public SessionManager(
        ISessionStore? store = null,
        ICompressionStrategy? compressor = null,
        ISessionHookManager? hooks = null,
        ILogger? logger = null)
    {
        _store = store;
        _compressor = compressor;
        _hooks = hooks;
        _logger = logger;
    }
    
    // === 核心操作 ===
    
    public SessionData Create(string? partitionId = null, AgentMetadata? agent = null)
    {
        var session = SessionData.Create(partitionId, agent);
        _sessions[session.Id] = session;
        
        _hooks?.Trigger(SessionHookPoints.Created, session);
        _logger?.LogInformation("创建 Session: {Id}", session.Id);
        
        return session;
    }
    
    public SessionData? Get(string id)
    {
        return _sessions.TryGetValue(id, out var session) ? session : null;
    }
    
    public bool Delete(string id)
    {
        if (_sessions.TryRemove(id, out var session))
        {
            _hooks?.Trigger(SessionHookPoints.Destroyed, session);
            _store?.DeleteAsync(id);  // 异步删除存储
            _logger?.LogInformation("删除 Session: {Id}", id);
            return true;
        }
        return false;
    }
    
    public IReadOnlyList<SessionData> List()
    {
        return _sessions.Values.ToList();
    }
    
    // === 扩展操作 ===
    
    public async Task SaveAsync(string id)
    {
        var session = Get(id);
        if (session == null || _store == null) return;
        
        _hooks?.Trigger(SessionHookPoints.Saving, session);
        await _store.SaveAsync(session);
        _hooks?.Trigger(SessionHookPoints.Saved, session);
        
        _logger?.LogInformation("保存 Session: {Id}, 消息数: {Count}", id, session.MessageCount);
    }
    
    public async Task<SessionData?> LoadAsync(string id)
    {
        if (_store == null) return null;
        
        var data = await _store.LoadAsync(id);
        if (data == null) return null;
        
        _sessions[id] = data;
        _hooks?.Trigger(SessionHookPoints.Loaded, data);
        
        _logger?.LogInformation("加载 Session: {Id}, 消息数: {Count}", id, data.MessageCount);
        return data;
    }
    
    public IReadOnlyList<SessionMessage> Compress(string id)
    {
        var session = Get(id);
        if (session == null || _compressor == null) return Array.Empty<SessionMessage>();
        
        var original = session.Messages;
        var compressed = _compressor.Compress(original);
        
        session.Messages.Clear();
        session.Messages.AddRange(compressed);
        
        _logger?.LogInformation("压缩 Session: {Id}, {Original} → {Compressed}", 
            id, original.Count, compressed.Count);
        
        return compressed;
    }
    
    // === 辅助方法 ===
    
    public SessionData? GetOrLoad(string id)
    {
        return Get(id) ?? LoadAsync(id).GetAwaiter().GetResult();
    }
    
    public async Task SaveAllAsync()
    {
        if (_store == null) return;
        
        foreach (var session in _sessions.Values)
        {
            await _store.SaveAsync(session);
        }
    }
}
```

**设计理由**：
- 组合优于继承：通过构造函数注入可选组件
- 默认可用：无参数构造也能工作（内存模式）
- 职责清晰：只协调，不做具体存储/压缩逻辑

### 4.2 FileSessionStore（文件存储）

```csharp
public class FileSessionStore : ISessionStore
{
    private readonly string _directory;
    private readonly JsonSerializerOptions _options;
    
    public FileSessionStore(string? directory = null)
    {
        _directory = directory ?? GetDefaultDirectory();
        _options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        
        Directory.CreateDirectory(_directory);
    }
    
    private static string GetDefaultDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".seeing", "sessions");
    }
    
    public async Task SaveAsync(SessionData data)
    {
        ValidateId(data.Id);
        var path = GetPath(data.Id);
        var json = JsonSerializer.Serialize(data, _options);
        await File.WriteAllTextAsync(path, json);
    }
    
    public async Task<SessionData?> LoadAsync(string id)
    {
        ValidateId(id);
        var path = GetPath(id);
        if (!File.Exists(path)) return null;
        
        var json = await File.ReadAllTextAsync(path);
        return JsonSerializer.Deserialize<SessionData>(json, _options);
    }
    
    public Task DeleteAsync(string id)
    {
        ValidateId(id);
        var path = GetPath(id);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }
    
    public async Task<IAsyncEnumerable<SessionData>> ListAsync()
    {
        return EnumerateSessions();
    }
    
    public Task<bool> ExistsAsync(string id)
    {
        ValidateId(id);
        return Task.FromResult(File.Exists(GetPath(id)));
    }
    
    private string GetPath(string id) => Path.Combine(_directory, $"{id}.json");
    
    private static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Session ID 不能为空");
        if (id.Contains("..") || id.Contains("/") || id.Contains("\\"))
            throw new ArgumentException("Session ID 包含非法路径字符");
    }
    
    private async IAsyncEnumerable<SessionData> EnumerateSessions()
    {
        foreach (var file in Directory.GetFiles(_directory, "*.json"))
        {
            var json = await File.ReadAllTextAsync(file);
            var data = JsonSerializer.Deserialize<SessionData>(json, _options);
            if (data != null) yield return data;
        }
    }
}
```

**设计理由**：
- 简洁实现：直接 JSON 文件存储
- 安全验证：路径遍历防护
- 默认位置：~/.seeing/sessions

### 4.3 SlidingWindowCompression（滑动窗口压缩）

```csharp
public class SlidingWindowCompression : ICompressionStrategy
{
    public string Name => "SlidingWindow";
    
    private readonly int _keepLastN;
    private readonly bool _keepSystem;
    
    public SlidingWindowCompression(int keepLastN = 20, bool keepSystem = true)
    {
        _keepLastN = keepLastN;
        _keepSystem = keepSystem;
    }
    
    public IReadOnlyList<SessionMessage> Compress(IReadOnlyList<SessionMessage> messages)
    {
        if (messages.Count <= _keepLastN + (_keepSystem ? 1 : 0))
            return messages;
        
        var result = new List<SessionMessage>();
        
        // 保留系统消息（第一条）
        if (_keepSystem && messages.Count > 0)
        {
            var first = messages[0];
            if (first.Role == MessageRole.System)
                result.Add(first);
        }
        
        // 保留最后 N 条
        var start = messages.Count - _keepLastN;
        for (int i = start; i < messages.Count; i++)
        {
            result.Add(messages[i]);
        }
        
        return result;
    }
    
    public int EstimateRetainedCount(int messageCount)
    {
        if (messageCount <= _keepLastN + (_keepSystem ? 1 : 0))
            return messageCount;
        return (_keepSystem ? 1 : 0) + _keepLastN;
    }
}
```

**设计理由**：
- 最简单有效：滑动窗口是唯一必要算法
- 配置简单：keepLastN + keepSystem
- 同步方法：纯计算无需异步

### 4.4 SessionHookManager（钩子管理）

```csharp
public class SessionHookManager : ISessionHookManager
{
    private readonly ConcurrentDictionary<string, List<ISessionHook>> _hooks = new();
    
    public void Register(ISessionHook hook)
    {
        var list = _hooks.GetOrAdd(hook.HookPoint, _ => new List<ISessionHook>());
        lock (list)
        {
            list.Add(hook);
            list.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        }
    }
    
    public bool Unregister(ISessionHook hook)
    {
        if (!_hooks.TryGetValue(hook.HookPoint, out var list)) return false;
        lock (list) return list.Remove(hook);
    }
    
    public Task Trigger(string hookPoint, SessionData? session = null)
    {
        if (!_hooks.TryGetValue(hookPoint, out var list)) return Task.CompletedTask;
        
        var context = new SessionHookContext
        {
            HookPoint = hookPoint,
            SessionId = session?.Id ?? "",
            Session = session
        };
        
        // 异步触发，不阻塞主流程
        _ = Task.Run(async () =>
        {
            foreach (var hook in list)
            {
                try { await hook.ExecuteAsync(context); }
                catch { /* 忽略 Hook 错误 */ }
            }
        });
        
        return Task.CompletedTask;
    }
}
```

**设计理由**：
- 职责单一：只管理钩子注册和触发
- 不阻塞：异步触发，不影响主流程
- 容错：Hook 错误不影响 Session 操作

---

## 五、Hook 点设计

```csharp
public static class SessionHookPoints
{
    // === 生命周期 ===
    public const string Created = "session.created";       // 创建后
    public const string Destroyed = "session.destroyed";   // 删除后
    
    // === 持久化 ===
    public const string Saving = "session.saving";         // 保存前
    public const string Saved = "session.saved";           // 保存后
    public const string Loaded = "session.loaded";         // 加载后
    
    // === 消息 ===
    public const string MessageAdded = "session.message_added";  // 消息添加后
    
    // === 压缩 ===
    public const string Compressed = "session.compressed";       // 压缩后
}
```

**设计理由**：
- 最小化：7 个 Hook 点覆盖核心生命周期
- 语义明确：每个点有清晰含义
- 无状态细分：Idle/Error 等通过 MessageAdded + Metadata 传递

---

## 六、与现有架构的关系

### 6.1 与 Agent 包的关系

```
Seeing.Agent (Agent 执行框架)
    │
    ├── AgentBase (执行器)
    │       └── 使用 SessionManager 获取 Session
    │       └── 执行后调用 session.AddMessage()
    │
    ├── ToolInvoker (工具调用)
    │       └── 调用 session.AddMessage() 记录工具调用
    │
    └── SessionManager (引用 Seeing.Session)
            │
            ▼
Seeing.Session (Session 管理框架 - 本方案)
    │
    ├── SessionData (数据模型)
    ├── ISessionStore (存储接口)
    ├── ICompressionStrategy (压缩接口)
    └── ISessionHook (钩子接口)
```

**设计理由**：
- Session 包完全独立
- Agent 包引用 Session 包，使用 SessionManager
- Agent 不直接操作 Session 内部结构

### 6.2 迁移策略

| 原有 | 替换为 |
|------|--------|
| SessionEntry | SessionData |
| SessionManager (Agent 包) | SessionManager (Session 包) |
| ISessionManager (Agent 包) | ISessionManager (Session 包) |
| SessionHookManager | SessionHookManager (简化版) |

**迁移步骤**：
1. SessionData 增强：添加 Messages
2. 删除 Agent 包的 SessionEntry
3. SessionManager 移至 Session 包
4. Agent 包引用 Session 包

---

## 七、执行计划

### Wave 1: 数据模型重构（1天）

```
Wave 1:
├── Task 1: 重构 SessionData（单一模型，含 Messages）
├── Task 2: 删除 SessionEntry（迁移到 SessionData）
├── Task 3: 创建 SessionData 工厂方法
├── Task 4: 更新 SessionStatus 枚举
└── Task 5: 编写 SessionData 单元测试
```

### Wave 2: 接口设计（1天）

```
Wave 2:
├── Task 6: 简化 ISessionStore（5 方法）
├── Task 7: 创建 ICompressionStrategy（3 方法）
├── Task 8: 简化 ISessionHook（2 方法 + 2 属性）
├── Task 9: 简化 ISessionManager（5 核心 + 3 扩展）
├── Task 10: 更新 SessionHookPoints（7 个）
└── Task 11: 编写接口测试
```

### Wave 3: 实现迁移（1天）

```
Wave 3:
├── Task 12: 实现 SessionManager（新设计）
├── Task 13: 实现 FileSessionStore（简化版）
├── Task 14: 实现 SlidingWindowCompression
├── Task 15: 实现 SessionHookManager（简化版）
├── Task 16: Agent 包迁移引用
└── Task 17: 编写集成测试
```

### Wave Final: 验证（半天）

```
Wave Final:
├── Task F1: 构建验证
├── Task F2: 测试验证（所有测试通过）
├── Task F3: Agent 包集成验证
└── Task F4: WebUI 适配验证
```

---

## 八、设计优势总结

| 维度 | 优化点 |
|------|--------|
| **模型简化** | 单一 SessionData，无双重模型 |
| **接口最小** | ISessionStore 5方法，ICompressionStrategy 3方法 |
| **职责清晰** | Session 管理 ≠ Agent 执行 |
| **可组合** | 构造函数注入可选组件 |
| **可替换** | Store/Compressor/Hook 均可替换 |
| **向后兼容** | SessionData 包含所有必要字段 |
| **线程安全** | ConcurrentDictionary 保证 |

---

## 九、最终架构图

```
Seeing.Session/
│
├── Core/
│   ├── SessionData.cs          # 单一数据模型
│   ├── SessionStatus.cs        # 状态枚举
│   ├── SessionMessage.cs       # 消息模型
│   ├── AgentMetadata.cs        # Agent 元信息
│   │
│   ├── ISessionManager.cs      # 管理器接口（5+3方法）
│   ├── ISessionStore.cs        # 存储接口（5方法）
│   ├── ICompressionStrategy.cs # 压缩接口（3方法）
│   └── ISessionHook.cs         # 钩子接口（2+2）
│
├── Storage/
│   ├── FileSessionStore.cs     # 文件存储实现
│   └── InMemorySessionStore.cs # 内存存储实现
│
├── Compression/
│   └── SlidingWindowCompression.cs
│
├── Hooks/
│   ├── SessionHookManager.cs
│   ├── SessionHookPoints.cs    # 7 个 Hook 点
│   └── SessionHookContext.cs
│
└── Management/
    └── SessionManager.cs       # 核心管理器
```

**文件数**: 14 个（精简）

---

## Success Criteria

```bash
# 构建
dotnet build src/Seeing.Session    # 0 errors, 0 warnings

# 测试
dotnet test tests/Seeing.Session.Tests   # all pass

# 集成
dotnet build src/Seeing.Agent      # 0 errors（引用 Session 包）
dotnet test tests/Seeing.Agent.Tests     # all pass
```

---

这个设计是否符合 Session 管理框架最优化的要求？