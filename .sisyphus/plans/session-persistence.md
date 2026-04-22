# Session 持久化最优设计方案

## TL;DR

> **核心目标**: 消息历史可持久化，最小改动，最大效果
>
> **设计原则**: 精简、聚焦、可扩展但不过度设计
>
> **关键改动**: 3个文件修改 + 1个接口新增
>
> **预计工作量**: Short（1天）
> **并行执行**: YES - 2 Waves

---

## 问题聚焦

| 问题 | 当前状态 | 目标状态 |
|------|----------|----------|
| 消息历史无法持久化 | SessionData 无 Messages | SessionData 含 Messages |
| 恢复会话丢失历史 | SessionManager 纯内存 | 可从存储恢复 |
| Hook 点语义混乱 | Saved 滥用 | 语义化 Hook 点 |

**不解决的问题**（推迟或有需求时）:
- ❌ 复杂持久化模式（Deferred/Hybrid）
- ❌ 压缩策略抽象
- ❌ ISession 接口统一
- ❌ Hook 点大量扩展

---

## 最优设计方案

### 核心设计决策

#### 1. SessionData 包含消息历史（用于序列化）

```csharp
public class SessionData
{
    // === 基础信息 ===
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string PartitionId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    
    // === Agent 信息 ===
    public AgentMetadata? Agent { get; set; }
    
    // === 状态 ===
    public SessionStatus Status { get; set; }
    
    // === 消息历史（核心新增） ===
    public List<SessionMessage> Messages { get; set; } = new();
    
    // === 上下文数据（改为 object 支持复杂类型） ===
    public Dictionary<string, object> Context { get; set; } = new();
}
```

#### 2. SessionEntry 保持线程安全 + 提供转换方法

```csharp
public class SessionEntry
{
    // 内部数据（线程安全）
    private readonly ConcurrentQueue<SessionMessage> _messages = new();
    private readonly ConcurrentDictionary<string, object> _context = new();
    
    // 基础属性
    public string SessionId { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime LastActiveAt { get; set; } = DateTime.UtcNow;
    public AgentMetadata? ActiveAgent { get; set; }
    public SessionStatus Status { get; set; } = SessionStatus.Unknown;
    
    // 消息操作（线程安全）
    public void AddMessage(SessionMessage message)
    {
        _messages.Enqueue(message);
        LastActiveAt = DateTime.UtcNow;
    }
    
    public IReadOnlyList<SessionMessage> Messages => _messages.ToList();
    
    public void ClearMessages()
    {
        _messages.Clear();  // ConcurrentQueue 需逐个取出，或原子替换
    }
    
    // 上下文操作（线程安全）
    public void SetContextValue(string key, object value) => _context[key] = value;
    public T? GetContextValue<T>(string key) => ...
    
    // === 核心新增：转换方法 ===
    
    /// <summary>
    /// 转换为可持久化的 SessionData（包含完整消息历史）
    /// </summary>
    public SessionData ToSessionData()
    {
        return new SessionData
        {
            Id = SessionId,
            Title = $"Session_{SessionId}",  // 可配置
            PartitionId = ActiveAgent?.AgentId ?? "default",
            CreatedAt = CreatedAt,
            UpdatedAt = LastActiveAt,
            Agent = ActiveAgent,
            Status = Status,
            Messages = Messages.ToList(),  // 快照
            Context = new Dictionary<string, object>(Context)
        };
    }
    
    /// <summary>
    /// 从 SessionData 创建 SessionEntry（恢复会话）
    /// </summary>
    public static SessionEntry FromSessionData(SessionData data)
    {
        var entry = new SessionEntry
        {
            SessionId = data.Id,
            CreatedAt = data.CreatedAt,
            LastActiveAt = data.UpdatedAt,
            ActiveAgent = data.Agent,
            Status = data.Status
        };
        
        // 恢复消息历史
        foreach (var msg in data.Messages)
        {
            entry.AddMessage(msg);
        }
        
        // 恢复上下文
        foreach (var (key, value) in data.Context)
        {
            entry.SetContextValue(key, value);
        }
        
        return entry;
    }
    
    /// <summary>
    /// 合并 SessionData 更新（增量更新）
    /// </summary>
    public void MergeFromSessionData(SessionData data)
    {
        Status = data.Status;
        LastActiveAt = data.UpdatedAt;
        
        // 只追加新消息（避免重复）
        var existingIds = Messages.Select(m => m.Id).ToHashSet();
        foreach (var msg in data.Messages.Where(m => !existingIds.Contains(m.Id)))
        {
            AddMessage(msg);
        }
    }
}
```

#### 3. ISessionStore 保持不变（无需扩展接口）

```csharp
// 当前接口已足够，无需修改
public interface ISessionStore
{
    Task SaveAsync(SessionData data);
    Task<SessionData?> LoadAsync(string sessionId);
    Task DeleteAsync(string sessionId);
    Task<IAsyncEnumerable<SessionData>> ListAsync();
    Task<IAsyncEnumerable<SessionData>> QueryAsync(string partitionId, string agentId);
    Task SaveAllAsync(IEnumerable<SessionData> data);
    Task<IAsyncEnumerable<SessionData>> LoadAllAsync();
}
```

**设计理由**：
- SessionData 现已包含 Messages
- `SaveAsync(SessionData)` 自然保存消息历史
- `LoadAsync(sessionId)` 自然恢复消息历史
- 无需新增 AppendMessageAsync 等方法

#### 4. SessionManager 增加持久化支持（最小改动）

```csharp
public class SessionManager : ISessionManager
{
    private readonly ILogger<SessionManager>? _logger;
    private readonly SessionHookManager _hookManager;
    private readonly ConcurrentDictionary<string, SessionEntry> _sessions = new();
    private readonly SessionCompressor _compressor = new();
    
    // === 核心新增：可选存储 ===
    private readonly ISessionStore? _store;
    
    /// <summary>
    /// 创建 SessionManager 实例
    /// </summary>
    /// <param name="hookManager">Hook 管理器</param>
    /// <param name="store">可选存储（提供时启用持久化）</param>
    /// <param name="logger">日志记录器</param>
    public SessionManager(
        SessionHookManager hookManager,
        ISessionStore? store = null,
        ILogger<SessionManager>? logger = null)
    {
        _hookManager = hookManager ?? throw new ArgumentNullException(nameof(hookManager));
        _store = store;
        _logger = logger;
    }
    
    // === 基础方法保持不变 ===
    
    public async Task<SessionEntry> CreateSessionAsync(...)
    {
        var session = ...;
        _sessions[sessionId] = session;
        
        // 触发 Hook
        await _hookManager.TriggerAsync(SessionHookPoints.Created, sessionId);
        
        return session;
    }
    
    public SessionEntry? GetSession(string sessionId) => _sessions.TryGetValue(sessionId, out var s) ? s : null;
    
    // === 核心新增：持久化方法 ===
    
    /// <summary>
    /// 保存会话到存储（如果配置了存储）
    /// </summary>
    public async Task<bool> SaveSessionAsync(string sessionId)
    {
        if (_store == null)
        {
            _logger?.LogWarning("未配置存储，无法保存会话: {SessionId}", sessionId);
            return false;
        }
        
        var session = GetSession(sessionId);
        if (session == null)
        {
            _logger?.LogWarning("会话不存在: {SessionId}", sessionId);
            return false;
        }
        
        // 触发保存前 Hook
        await _hookManager.TriggerAsync(SessionHookPoints.Saving, sessionId);
        
        // 转换并保存
        var data = session.ToSessionData();
        await _store.SaveAsync(data);
        
        // 触发保存后 Hook
        await _hookManager.TriggerAsync(SessionHookPoints.Saved, sessionId);
        
        _logger?.LogInformation("保存会话成功: {SessionId}, 消息数: {Count}", sessionId, data.Messages.Count);
        return true;
    }
    
    /// <summary>
    /// 从存储恢复会话（如果配置了存储）
    /// </summary>
    public async Task<SessionEntry?> RestoreSessionAsync(string sessionId)
    {
        if (_store == null)
        {
            _logger?.LogWarning("未配置存储，无法恢复会话: {SessionId}", sessionId);
            return null;
        }
        
        // 从存储加载
        var data = await _store.LoadAsync(sessionId);
        if (data == null)
        {
            _logger?.LogWarning("存储中不存在会话: {SessionId}", sessionId);
            return null;
        }
        
        // 转换为 SessionEntry
        var session = SessionEntry.FromSessionData(data);
        
        // 加入内存管理
        _sessions[sessionId] = session;
        
        // 触发恢复 Hook
        await _hookManager.TriggerAsync(SessionHookPoints.Loaded, sessionId);
        
        _logger?.LogInformation("恢复会话成功: {SessionId}, 消息数: {Count}", sessionId, data.Messages.Count);
        return session;
    }
    
    /// <summary>
    /// 获取或恢复会话（内存优先，存储次之）
    /// </summary>
    public async Task<SessionEntry?> GetOrRestoreSessionAsync(string sessionId)
    {
        // 优先从内存获取
        var session = GetSession(sessionId);
        if (session != null)
        {
            session.LastActiveAt = DateTime.UtcNow;
            return session;
        }
        
        // 内存不存在，尝试从存储恢复
        return await RestoreSessionAsync(sessionId);
    }
    
    /// <summary>
    /// 保存所有活跃会话
    /// </summary>
    public async Task SaveAllSessionsAsync()
    {
        if (_store == null) return;
        
        var datas = _sessions.Values.Select(s => s.ToSessionData()).ToList();
        await _store.SaveAllAsync(datas);
        
        _logger?.LogInformation("保存所有会话: {Count} 个", datas.Count);
    }
    
    /// <summary>
    /// 列出存储中的所有会话（用于启动时恢复）
    /// </summary>
    public async Task<List<SessionData>> ListStoredSessionsAsync()
    {
        if (_store == null) return new List<SessionData>();
        
        var result = new List<SessionData>();
        await foreach (var data in _store.ListAsync())
        {
            result.Add(data);
        }
        return result;
    }
}
```

#### 5. Hook 点最小扩展

```csharp
public static class SessionHookPoints
{
    // === 生命周期 ===
    public const string Created = "session.created";
    public const string Destroyed = "session.destroyed";
    public const string Saving = "session.saving";
    public const string Saved = "session.saved";
    public const string Loaded = "session.loaded";      // 新增（恢复时）
    
    // === 消息操作（核心新增，替代 Saved 滥用） ===
    public const string MessageAdded = "session.message_added";
}
```

**改动**：SessionManager.AddMessageAsync 触发 MessageAdded 而非 Saved：

```csharp
public async Task AddMessageAsync(string sessionId, SessionMessage message)
{
    var session = GetSession(sessionId);
    if (session == null) return;
    
    session.AddMessage(message);
    
    // 触发 MessageAdded Hook（语义明确）
    await _hookManager.TriggerAsync(SessionHookPoints.MessageAdded, sessionId);
}
```

---

## 执行策略

### Wave 1: 数据模型增强（并行执行）

```
Wave 1:
├── Task 1: 增强 SessionData（添加 Messages/Context）
├── Task 2: SessionEntry 增加 ToSessionData/FromSessionData 方法
├── Task 3: SessionEntry 增加 ClearMessages 原子替换实现
└── Task 4: 更新 SessionStatus 增加 Idle/Error（可选）
```

### Wave 2: 持久化集成

```
Wave 2（依赖 Wave 1）:
├── Task 5: SessionManager 增加 _store 可选参数
├── Task 6: SessionManager 增加 SaveSessionAsync 方法
├── Task 7: SessionManager 增加 RestoreSessionAsync 方法
├── Task 8: SessionManager 增加 GetOrRestoreSessionAsync 方法
├── Task 9: SessionHookPoints 增加 MessageAdded 常量
├── Task 10: SessionManager.AddMessageAsync 改用 MessageAdded Hook
└── Task 11: FileSessionStore 验证 SessionData 消息序列化
```

### Wave Final: 验证

```
Wave Final:
├── Task F1: 持久化流程测试
├── Task F2: Hook 触发验证
├── Task F3: 向后兼容验证
└── Task F4: 构建和测试全部通过
```

---

## TODOs

- [ ] 1. 增强 SessionData 模型

  **What to do**:
  - `SessionData.cs` 添加 `List<SessionMessage> Messages` 字段
  - `Context` 类型改为 `Dictionary<string, object>`
  
  **Recommended Agent Profile**:
  - Category: `quick`
  - Skills: []

  **Parallelization**: Wave 1 (with Tasks 2-4)

- [ ] 2. SessionEntry 增加 ToSessionData/FromSessionData 方法

  **What to do**:
  - `SessionEntry.cs` 添加转换方法
  - ToSessionData: 返回完整快照（含消息）
  - FromSessionData: 从数据恢复（含消息）
  
  **Recommended Agent Profile**:
  - Category: `quick`
  - Skills: []

  **Parallelization**: Wave 1

- [ ] 3. SessionEntry.ClearMessages 原子替换

  **What to do**:
  - ConcurrentQueue 无 Clear 方法
  - 使用原子替换 `_messages = new ConcurrentQueue<>()`
  - 需要使用 `volatile` 或锁保证线程安全
  
  **Recommended Agent Profile**:
  - Category: `quick`
  - Skills: []

  **Parallelization**: Wave 1

- [ ] 4. SessionStatus 增加 Idle/Error（可选）

  **What to do**:
  - 添加 `Idle = 3`, `Error = 5` 状态
  - 更新 XML 注释
  
  **Recommended Agent Profile**:
  - Category: `quick`
  - Skills: []

  **Parallelization**: Wave 1

---

- [ ] 5. SessionManager 增加 _store 可选参数

  **What to do**:
  - 构造函数添加 `ISessionStore? store = null` 参数
  - 保存到私有字段
  
  **Recommended Agent Profile**:
  - Category: `quick`
  - Skills: []

  **Parallelization**: Wave 2

- [ ] 6. SessionManager.SaveSessionAsync

  **What to do**:
  - 检查 _store 是否配置
  - 调用 session.ToSessionData()
  - 调用 _store.SaveAsync(data)
  
  **Recommended Agent Profile**:
  - Category: `quick`
  - Skills: []

  **Parallelization**: Wave 2

- [ ] 7. SessionManager.RestoreSessionAsync

  **What to do**:
  - 调用 _store.LoadAsync(sessionId)
  - 调用 SessionEntry.FromSessionData(data)
  - 加入内存管理
  
  **Recommended Agent Profile**:
  - Category: `quick`
  - Skills: []

  **Parallelization**: Wave 2

- [ ] 8. SessionManager.GetOrRestoreSessionAsync

  **What to do**:
  - 优先内存获取
  - 内存不存在则从存储恢复
  
  **Recommended Agent Profile**:
  - Category: `quick`
  - Skills: []

  **Parallelization**: Wave 2

- [ ] 9. SessionHookPoints 增加 MessageAdded

  **What to do**:
  - 添加 `MessageAdded = "session.message_added"` 常量
  
  **Recommended Agent Profile**:
  - Category: `quick`
  - Skills: []

  **Parallelization**: Wave 2

- [ ] 10. SessionManager.AddMessageAsync 改用 MessageAdded Hook

  **What to do**:
  - 将 Saved Hook 替换为 MessageAdded
  
  **Recommended Agent Profile**:
  - Category: `quick`
  - Skills: []

  **Parallelization**: Wave 2

- [ ] 11. FileSessionStore 验证消息序列化

  **What to do**:
  - 确保 SessionMessage 正确序列化为 JSON
  - 验证多模态消息（Parts、ToolCalls）序列化
  
  **Recommended Agent Profile**:
  - Category: `quick`
  - Skills: []

  **Parallelization**: Wave 2

---

- [ ] F1. 持久化流程测试

  **What to do**:
  - 测试完整流程：创建 → 添加消息 → 保存 → 恢复
  - 验证消息历史完整恢复
  
  **Recommended Agent Profile**:
  - Category: `unspecified-high`
  - Skills: []

- [ ] F2. Hook 触发验证

  **What to do**:
  - 验证 MessageAdded Hook 正确触发
  - 验证 Saved/Loaded Hook 正确触发
  
  **Recommended Agent Profile**:
  - Category: `quick`
  - Skills: []

- [ ] F3. 向后兼容验证

  **What to do**:
  - 验证现有 SessionManager 构造函数（无 store）行为不变
  - 验证 SessionEntry 公开 API 不变
  
  **Recommended Agent Profile**:
  - Category: `quick`
  - Skills: []

- [ ] F4. 构建和测试全部通过

  **What to do**:
  - dotnet build src/Seeing.Session
  - dotnet test tests/Seeing.Session.Tests
  - dotnet test tests/Seeing.Agent.Tests
  
  **Recommended Agent Profile**:
  - Category: `quick`
  - Skills: []

---

## 使用示例

### 基础使用（内存模式，向后兼容）

```csharp
// 默认构造（无存储，纯内存）
var sessionManager = new SessionManager(hookManager);

var session = sessionManager.CreateSession("agent-1");
session.AddMessage(SessionMessage.UserMessage("Hello"));

// 消息在内存中，重启丢失
```

### 持久化使用

```csharp
// 配置存储
var sessionManager = new SessionManager(
    hookManager,
    new FileSessionStore());  // 启用持久化

// 创建会话并添加消息
var session = sessionManager.CreateSession("agent-1");
session.AddMessage(SessionMessage.UserMessage("Hello"));
session.AddMessage(SessionMessage.AssistantMessage("Hi there!"));

// 保存到文件
await sessionManager.SaveSessionAsync(session.SessionId);

// 应用重启后恢复
var restored = await sessionManager.RestoreSessionAsync(session.SessionId);
// restored.Messages 包含完整历史
```

### 启动时恢复所有会话

```csharp
// 应用启动时
var sessionManager = new SessionManager(hookManager, new FileSessionStore());

// 恢复所有已存储会话
var storedSessions = await sessionManager.ListStoredSessionsAsync();
foreach (var data in storedSessions)
{
    var session = await sessionManager.RestoreSessionAsync(data.Id);
    // 恢复历史，继续对话
}
```

---

## Commit Strategy

- **Wave 1**: `feat(session): add messages to SessionData for persistence`
- **Wave 2**: `feat(session): add persistence support to SessionManager`
- **Final**: `test(session): verify persistence flow complete`

---

## Success Criteria

### 验证命令
```bash
dotnet build src/Seeing.Session           # 0 errors
dotnet test tests/Seeing.Session.Tests    # all pass
dotnet test tests/Seeing.Agent.Tests      # all pass (向后兼容)
```

### 最终检查
- [ ] SessionData 包含 Messages 字段
- [ ] SessionEntry.ToSessionData/FromSessionData 实现正确
- [ ] SessionManager.SaveSessionAsync 工作正常
- [ ] SessionManager.RestoreSessionAsync 恢复完整历史
- [ ] FileSessionStore 序列化消息历史
- [ ] 向后兼容验证通过

---

## 设计优势

| 维度 | 说明 |
|------|------|
| **最小改动** | 仅 3 个文件 + 1 个常量 |
| **向后兼容** | 默认构造行为不变 |
| **接口简洁** | ISessionStore 无需扩展 |
| **线程安全** | SessionEntry 保持 ConcurrentQueue |
| **可扩展** | 后续可按需添加复杂模式 |

---

## 后续扩展点（按需）

| 扩展 | 触发条件 |
|------|----------|
| Deferred/Hybrid 模式 | 有批量/定时保存需求 |
| IMessageCompressionStrategy | 有第二种压缩算法需求 |
| 更多 Hook 点 | 有外部集成需求 |
| ISession 统一 | 有接口抽象需求 |