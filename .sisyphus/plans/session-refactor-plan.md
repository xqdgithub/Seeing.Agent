# Session 管理框架重构计划（路径B）

## TL;DR

> **重构目标**: 按照最优化设计方案重构现有实现
>
> **核心改动**: 
> - SessionData 合并 SessionEntry（单一数据模型）
> - ISessionManager 简化（移除同步版本，≤10方法）
> - 抽取 ICompressionStrategy 接口
> - 补全 Hook 点
>
> **预计工作量**: Medium（1-2天）
> **并行执行**: YES - 3 Waves
> **关键路径**: Wave 1（数据模型合并） → Wave 2（接口简化） → Wave 3（补全功能）

---

## 重构清单

| # | 改动 | 严重性 | 影响 |
|---|------|--------|------|
| 1 | SessionData 合并 SessionEntry | HIGH | 单一数据模型 |
| 2 | ISessionManager 简化 | HIGH | 移除同步版本，≤10方法 |
| 3 | 抽取 ICompressionStrategy | MEDIUM | 可替换压缩算法 |
| 4 | 补全 Hook 点 | MEDIUM | MessageAdded, Compressed |
| 5 | 统一 SessionStatus | LOW | 对齐设计/实现 |
| 6 | SessionManager 构造函数调整 | MEDIUM | 可选组件注入 |

---

## 重构详情

### 改动 1: SessionData 合并 SessionEntry

#### 当前状态

```
SessionData（存储模型）- 25行
├── Id, Title, PartitionId
├── CreatedAt, UpdatedAt
├── Agent, Status
├── State: Dictionary<string, string>
└── ❌ 无 Messages

SessionEntry（运行时模型）- 103行
├── SessionId
├── CreatedAt, LastActiveAt
├── ActiveAgent
├── WorkingDirectory
├── Messages: ConcurrentQueue<SessionMessage> ✓
├── Context: ConcurrentDictionary<string, object> ✓
└── AddMessage(), GetContextValue<T>(), ClearMessages()
```

#### 目标状态

```
SessionData（单一模型）
├── Id, Title, PartitionId
├── CreatedAt, UpdatedAt, LastActiveAt
├── Agent (AgentMetadata)
├── WorkingDirectory
├── Status (SessionStatus)
├── Messages: List<SessionMessage> ✓
├── Context: Dictionary<string, object> ✓
├── Metadata: Dictionary<string, string>
├── AddMessage(message)
├── SetContext(key, value)
├── GetContext<T>(key)
├── ClearMessages()
├── ToSnapshot() / FromSnapshot()
└── 静态 Create() 工厂方法
```

#### 实现要点

```csharp
public class SessionData
{
    // === 身份信息 ===
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string PartitionId { get; set; } = string.Empty;
    
    // === 时间信息 ===
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime LastActiveAt { get; set; }
    
    // === Agent 信息 ===
    public AgentMetadata? Agent { get; set; }
    public string? WorkingDirectory { get; set; }
    
    // === 状态 ===
    public SessionStatus Status { get; set; } = SessionStatus.Created;
    
    // === 消息历史 ===
    public List<SessionMessage> Messages { get; set; } = new();
    
    // === 上下文数据 ===
    public Dictionary<string, object> Context { get; set; } = new();
    
    // === 元数据 ===
    public Dictionary<string, string> Metadata { get; set; } = new();
    
    // === 统计属性 ===
    public int MessageCount => Messages.Count;
    
    // === 工厂方法 ===
    public static SessionData Create(string? partitionId = null, AgentMetadata? agent = null)
    {
        var id = $"ses_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}[..8]}";
        return new SessionData
        {
            Id = id,
            Title = $"Session {id}",
            PartitionId = partitionId ?? "default",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            LastActiveAt = DateTime.UtcNow,
            Agent = agent,
            Status = SessionStatus.Created
        };
    }
    
    // === 操作方法 ===
    public void AddMessage(SessionMessage message)
    {
        Messages.Add(message ?? throw new ArgumentNullException(nameof(message)));
        UpdatedAt = DateTime.UtcNow;
        LastActiveAt = DateTime.UtcNow;
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
    
    public bool TryGetContext<T>(string key, out T? value)
    {
        if (Context.TryGetValue(key, out var obj) && obj is T typed)
        {
            value = typed;
            return true;
        }
        value = default;
        return false;
    }
    
    public void ClearMessages()
    {
        Messages.Clear();
        UpdatedAt = DateTime.UtcNow;
    }
    
    public void RemoveContext(string key)
    {
        Context.Remove(key);
        UpdatedAt = DateTime.UtcNow;
    }
    
    // === 快照方法（用于线程安全场景）===
    public SessionData Clone()
    {
        return new SessionData
        {
            Id = Id,
            Title = Title,
            PartitionId = PartitionId,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            LastActiveAt = LastActiveAt,
            Agent = Agent,
            WorkingDirectory = WorkingDirectory,
            Status = Status,
            Messages = Messages.ToList(),
            Context = new Dictionary<string, object>(Context),
            Metadata = new Dictionary<string, string>(Metadata)
        };
    }
}
```

#### 删除文件

- `src/Seeing.Session/Core/SessionEntry.cs` - 删除

---

### 改动 2: ISessionManager 简化

#### 当前状态

```csharp
public interface ISessionManager
{
    // 17 个方法
    Task<SessionEntry> CreateSessionAsync(string? agentId, string? agentName, string? workingDirectory);
    SessionEntry CreateSession(string? agentId, string? agentName);  // 同步版本
    SessionEntry? GetSession(string sessionId);
    SessionEntry GetOrCreateSession(string? sessionId, string? agentId, string? agentName);
    Task<bool> DeleteSessionAsync(string sessionId);
    bool DeleteSession(string sessionId);  // 同步版本
    Task AddMessageAsync(string sessionId, SessionMessage message);
    void AddMessage(string sessionId, SessionMessage message);  // 同步版本
    IReadOnlyList<SessionMessage> GetMessages(string sessionId);
    Task SetContextAsync(string sessionId, string key, object value);
    void SetContext(string sessionId, string key, object value);  // 同步版本
    T? GetContext<T>(string sessionId, string key);
    IReadOnlyCollection<SessionEntry> GetActiveSessions();
    Task CleanupExpiredSessionsAsync(TimeSpan expiration);
    void CleanupExpiredSessions(TimeSpan expiration);  // 同步版本
    Task SetIdleAsync(string sessionId);
    Task SetErrorAsync(string sessionId, Exception error);
    Task<int> CompactAsync(string sessionId, CancellationToken cancellationToken);
}
```

#### 目标状态

```csharp
public interface ISessionManager
{
    // === 核心操作（5方法）===
    SessionData Create(string? partitionId = null, AgentMetadata? agent = null);
    SessionData? Get(string id);
    bool Delete(string id);
    IReadOnlyList<SessionData> List();
    
    // === 扩展操作（依赖可选组件）===
    Task SaveAsync(string id);                     // 持久化（需要 Store）
    Task<SessionData?> LoadAsync(string id);        // 恢复（需要 Store）
    IReadOnlyList<SessionMessage> Compress(string id);  // 压缩（需要 Compressor）
    
    // === 辅助操作（保留）===
    Task<SessionData?> GetOrLoadAsync(string id);   // 内存优先，存储次之
    Task CleanupAsync(TimeSpan expiration);         // 清理过期
}
```

**总计**: 8 方法（核心5 + 扩展3）

#### 移除方法及替代方案

| 移除方法 | 替代方案 |
|----------|----------|
| CreateSession/CreateSessionAsync | `Create()` |
| GetSession | `Get()` |
| GetOrCreateSession | `GetOrLoadAsync()` |
| DeleteSession/DeleteSessionAsync | `Delete()` |
| AddMessage/AddMessageAsync | `session.AddMessage()` 直接操作 |
| GetMessages | `session.Messages` 直接访问 |
| SetContext/SetContextAsync | `session.SetContext()` 直接操作 |
| GetContext | `session.GetContext<T>()` 直接访问 |
| GetActiveSessions | `List()` |
| CleanupExpiredSessions/CleanupExpiredSessionsAsync | `CleanupAsync()` |
| SetIdleAsync | `session.Status = SessionStatus.Idle` |
| SetErrorAsync | `session.Status = SessionStatus.Error` + Metadata["error"] |
| CompactAsync | `Compress()` |

---

### 改动 3: 抽取 ICompressionStrategy 接口

#### 新增接口

```csharp
namespace Seeing.Session.Compression
{
    /// <summary>
    /// 消息压缩策略接口
    /// </summary>
    public interface ICompressionStrategy
    {
        /// <summary>策略名称</summary>
        string Name { get; }
        
        /// <summary>压缩消息列表</summary>
        IReadOnlyList<SessionMessage> Compress(IReadOnlyList<SessionMessage> messages);
        
        /// <summary>估算压缩后保留的消息数量</summary>
        int EstimateRetainedCount(int messageCount);
    }
}
```

#### 重构 SessionCompressor

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
        if (_keepSystem && messages.Count > 0 && messages[0].Role == MessageRole.System)
            result.Add(messages[0]);
        
        // 保留最后 N 条
        var start = messages.Count - _keepLastN;
        for (int i = start; i < messages.Count; i++)
            result.Add(messages[i]);
        
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

---

### 改动 4: 补全 Hook 点

#### 当前状态

```csharp
public static class SessionHookPoints
{
    public const string Created = "session.created";
    public const string Destroyed = "session.destroyed";
    public const string Saving = "session.saving";
    public const string Saved = "session.saved";
    public const string Loading = "session.loading";
    public const string Loaded = "session.loaded";
}
```

#### 目标状态

```csharp
public static class SessionHookPoints
{
    // === 生命周期 ===
    public const string Created = "session.created";
    public const string Destroyed = "session.destroyed";
    
    // === 持久化 ===
    public const string Saving = "session.saving";
    public const string Saved = "session.saved";
    public const string Loading = "session.loading";
    public const string Loaded = "session.loaded";
    
    // === 消息操作（新增）===
    public const string MessageAdded = "session.message_added";
    
    // === 压缩操作（新增）===
    public const string Compressed = "session.compressed";
}
```

**总计**: 8 个 Hook 点

---

### 改动 5: 统一 SessionStatus

#### 当前状态

```csharp
public enum SessionStatus
{
    Unknown = 0,
    Running = 1,
    Paused = 2,
    Completed = 3
}
```

#### 目标状态

```csharp
public enum SessionStatus
{
    Created = 0,      // 已创建（新增）
    Active = 1,       // 活跃（替换 Running）
    Idle = 2,         // 空闲（替换 Paused）
    Completed = 3,    // 已完成
    Archived = 4,     // 已归档（新增）
    Error = 5         // 错误状态（新增）
}
```

---

### 改动 6: SessionManager 重构

#### 目标实现

```csharp
public class SessionManager : ISessionManager
{
    // === 内部存储 ===
    private readonly ConcurrentDictionary<string, SessionData> _sessions = new();
    
    // === 可选组件 ===
    private readonly ISessionStore? _store;
    private readonly ICompressionStrategy? _compressor;
    private readonly ISessionHookManager? _hooks;
    private readonly ILogger<SessionManager>? _logger;
    
    // === 构造函数（可组合）===
    public SessionManager(
        ISessionStore? store = null,
        ICompressionStrategy? compressor = null,
        ISessionHookManager? hooks = null,
        ILogger<SessionManager>? logger = null)
    {
        _store = store;
        _compressor = compressor ?? new SlidingWindowCompression();
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
            
            // 异步删除存储（不阻塞）
            if (_store != null)
                _ = _store.DeleteAsync(id);
            
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
        if (session == null)
        {
            _logger?.LogWarning("Session 不存在: {Id}", id);
            return;
        }
        
        if (_store == null)
        {
            _logger?.LogWarning("未配置存储，无法保存: {Id}", id);
            return;
        }
        
        _hooks?.Trigger(SessionHookPoints.Saving, session);
        
        // 克隆快照保存（避免并发修改）
        var snapshot = session.Clone();
        await _store.SaveAsync(snapshot);
        
        _hooks?.Trigger(SessionHookPoints.Saved, session);
        _logger?.LogInformation("保存 Session: {Id}, 消息数: {Count}", id, snapshot.MessageCount);
    }
    
    public async Task<SessionData?> LoadAsync(string id)
    {
        if (_store == null)
        {
            _logger?.LogWarning("未配置存储，无法加载: {Id}", id);
            return null;
        }
        
        var data = await _store.LoadAsync(id);
        if (data == null)
        {
            _logger?.LogWarning("存储中不存在: {Id}", id);
            return null;
        }
        
        _sessions[data.Id] = data;
        _hooks?.Trigger(SessionHookPoints.Loaded, data);
        _logger?.LogInformation("加载 Session: {Id}, 消息数: {Count}", id, data.MessageCount);
        
        return data;
    }
    
    public IReadOnlyList<SessionMessage> Compress(string id)
    {
        var session = Get(id);
        if (session == null)
        {
            _logger?.LogWarning("Session 不存在: {Id}", id);
            return Array.Empty<SessionMessage>();
        }
        
        if (_compressor == null)
        {
            _logger?.LogWarning("未配置压缩器: {Id}", id);
            return session.Messages;
        }
        
        var original = session.Messages;
        var compressed = _compressor.Compress(original);
        
        session.Messages.Clear();
        session.Messages.AddRange(compressed);
        session.UpdatedAt = DateTime.UtcNow;
        
        _hooks?.Trigger(SessionHookPoints.Compressed, session);
        _logger?.LogInformation("压缩 Session: {Id}, {Original} → {Compressed}", 
            id, original.Count, compressed.Count);
        
        return compressed;
    }
    
    // === 辅助操作 ===
    
    public async Task<SessionData?> GetOrLoadAsync(string id)
    {
        var session = Get(id);
        if (session != null)
        {
            session.LastActiveAt = DateTime.UtcNow;
            return session;
        }
        
        return await LoadAsync(id);
    }
    
    public async Task CleanupAsync(TimeSpan expiration)
    {
        var threshold = DateTime.UtcNow - expiration;
        var expired = _sessions.Values
            .Where(s => s.LastActiveAt < threshold)
            .Select(s => s.Id)
            .ToList();
        
        foreach (var id in expired)
        {
            await SaveAsync(id);  // 先保存
            Delete(id);           // 再从内存移除
        }
        
        if (expired.Count > 0)
            _logger?.LogInformation("清理过期 Session: {Count} 个", expired.Count);
    }
    
    // === 批量操作 ===
    
    public async Task SaveAllAsync()
    {
        if (_store == null) return;
        
        foreach (var session in _sessions.Values)
        {
            await _store.SaveAsync(session.Clone());
        }
        
        _logger?.LogInformation("保存所有 Session: {Count} 个", _sessions.Count);
    }
}
```

---

## 执行策略

### Wave 1: 数据模型合并（并行执行）

```
Wave 1:
├── Task 1: 重构 SessionData（添加 Messages/Context/操作方法）
├── Task 2: 添加 SessionData.Create() 工厂方法
├── Task 3: 添加 SessionData.Clone() 快照方法
├── Task 4: 更新 SessionStatus 枚举（6状态）
├── Task 5: 删除 SessionEntry.cs
└── Task 6: 编写 SessionData 单元测试
```

**预计时间**: 3-4小时

### Wave 2: 接口简化（依赖 Wave 1）

```
Wave 2:
├── Task 7: 简化 ISessionManager（8方法）
├── Task 8: 创建 ICompressionStrategy 接口
├── Task 9: 重构 SlidingWindowCompression 实现接口
├── Task 10: 补全 SessionHookPoints（8个）
├── Task 11: 简化 SessionHookManager
└── Task 12: 编写接口测试
```

**预计时间**: 3-4小时

### Wave 3: 管理器重构（依赖 Wave 2）

```
Wave 3:
├── Task 13: 重构 SessionManager（新构造函数）
├── Task 14: 实现 SessionManager 核心操作
├── Task 15: 实现 SessionManager 扩展操作
├── Task 16: 更新 FileSessionStore 序列化
├── Task 17: Agent 包迁移（更新引用）
└── Task 18: 编写集成测试
```

**预计时间**: 4-5小时

### Wave Final: 验证

```
Wave Final:
├── Task F1: 构建验证（0 errors）
├── Task F2: 测试验证（所有测试通过）
├── Task F3: Agent 包集成验证
├── Task F4: WebUI 适配验证
└── Task F5: API 变更文档
```

**预计时间**: 1-2小时

---

## TODOs

### Wave 1: 数据模型合并

- [x] 1. 重构 SessionData（添加 Messages/Context/操作方法）

  **What to do**:
  - `SessionData.cs` 添加 `List<SessionMessage> Messages` 字段
  - 添加 `Dictionary<string, object> Context` 字段
  - 添加 `LastActiveAt`, `WorkingDirectory` 字段
  - 添加 `AddMessage()`, `SetContext()`, `GetContext<T>()`, `ClearMessages()` 方法
  
  **QA Scenarios**:
  ```
  Scenario: 消息操作
    Tool: Bash (dotnet test)
    Steps:
      1. 创建 SessionData
      2. 调用 AddMessage() 5次
      3. 验证 Messages.Count = 5
    Expected: PASS
    Evidence: .sisyphus/evidence/task-1-messages.txt
  ```

  **Recommended Agent Profile**:
  - Category: `quick`
  - Skills: []
  - Parallelization: Wave 1

- [x] 2. 添加 SessionData.Create() 工厂方法

  **What to do**:
  - 添加静态 `Create(string? partitionId, AgentMetadata? agent)` 方法
  - 自动生成 ID 格式：`ses_yyyyMMddHHmmss_xxxxxxxx`
  
  **Recommended Agent Profile**:
  - Category: `quick`
  - Skills: []
  - Parallelization: Wave 1

- [x] 3. 添加 SessionData.Clone() 快照方法

  **What to do**:
  - 添加 `Clone()` 方法返回深拷贝
  - 用于线程安全保存
  
  **Recommended Agent Profile**:
  - Category: `quick`
  - Skills: []
  - Parallelization: Wave 1

- [x] 4. 更新 SessionStatus 枚举

  **What to do**:
  - 改为：Created/Active/Idle/Completed/Archived/Error（6状态）
  - 更新 XML 注释
  
  **Recommended Agent Profile**:
  - Category: `quick`
  - Skills: []
  - Parallelization: Wave 1

- [x] 5. 删除 SessionEntry.cs

  **What to do**:
  - 删除 `src/Seeing.Session/Core/SessionEntry.cs`
  - 更新所有引用
  
  **Recommended Agent Profile**:
  - Category: `quick`
  - Skills: []
  - Parallelization: Wave 1

- [x] 6. 编写 SessionData 单元测试

  **What to do**:
  - 测试 Create() 工厂方法
  - 测试 AddMessage() 操作
  - 测试 Clone() 快照
  
  **Recommended Agent Profile**:
  - Category: `quick`
  - Skills: []
  - Parallelization: Wave 1

---

### Wave 2: 接口简化

- [x] 7. 简化 ISessionManager（8方法）

  **What to do**:
  - 核心：Create/Get/Delete/List（4方法）
  - 扩展：SaveAsync/LoadAsync/Compress（3方法）
  - 辅助：GetOrLoadAsync/CleanupAsync（可选）
  - 删除同步版本和状态管理方法
  
  **Recommended Agent Profile**:
  - Category: `quick`
  - Skills: []
  - Parallelization: Wave 2

- [x] 8. 创建 ICompressionStrategy 接口

  **What to do**:
  - 新建 `Compression/ICompressionStrategy.cs`
  - 定义：Name, Compress(), EstimateRetainedCount()
  
  **Recommended Agent Profile**:
  - Category: `quick`
  - Skills: []
  - Parallelization: Wave 2

- [x] 9. 重构 SlidingWindowCompression 实现接口

  **What to do**:
  - 重命名 `SessionCompressor.cs` → `SlidingWindowCompression.cs`
  - 实现 `ICompressionStrategy` 接口
  
  **Recommended Agent Profile**:
  - Category: `quick`
  - Skills: []
  - Parallelization: Wave 2

- [x] 10. 补全 SessionHookPoints（8个）

  **What to do**:
  - 添加 `MessageAdded = "session.message_added"`
  - 添加 `Compressed = "session.compressed"`
  
  **Recommended Agent Profile**:
  - Category: `quick`
  - Skills: []
  - Parallelization: Wave 2

- [x] 11. 简化 SessionHookManager

  **What to do**:
  - 简化 Trigger 方法签名
  - 支持 SessionData 参数
  
  **Recommended Agent Profile**:
  - Category: `quick`
  - Skills: []
  - Parallelization: Wave 2

- [x] 12. 编写接口测试

  **What to do**:
  - 测试 ISessionManager 新接口
  - 测试 ICompressionStrategy
  
  **Recommended Agent Profile**:
  - Category: `quick`
  - Skills: []
  - Parallelization: Wave 2

---

### Wave 3: 管理器重构

- [x] 13. 重构 SessionManager（新构造函数）

  **What to do**:
  - 构造函数改为可选组件注入：
    - `ISessionStore? store`
    - `ICompressionStrategy? compressor`
    - `ISessionHookManager? hooks`
    - `ILogger<SessionManager>? logger`
  
  **Recommended Agent Profile**:
  - Category: `deep`
  - Skills: []
  - Parallelization: Wave 3

- [x] 14. 实现 SessionManager 核心操作

  **What to do**:
  - Create/Get/Delete/List
  - 使用 SessionData 替代 SessionEntry
  
  **Recommended Agent Profile**:
  - Category: `quick`
  - Skills: []
  - Parallelization: Wave 3

- [x] 15. 实现 SessionManager 扩展操作

  **What to do**:
  - SaveAsync/LoadAsync（调用 Store）
  - Compress（调用 Compressor）
  - GetOrLoadAsync/CleanupAsync
  
  **Recommended Agent Profile**:
  - Category: `quick`
  - Skills: []
  - Parallelization: Wave 3

- [x] 16. 更新 FileSessionStore 序列化

  **What to do**:
  - 验证 SessionData.Messages 序列化
  - 验证 Context<object> 序列化
  
  **Recommended Agent Profile**:
  - Category: `quick`
  - Skills: []
  - Parallelization: Wave 3

- [x] 17. Agent 包迁移

  **What to do**:
  - 更新 `ServiceCollectionExtensions.cs` DI 注册
  - 更新 TaskTool/TodoWriteTool 引用
  - SessionEntry → SessionData
  
  **Recommended Agent Profile**:
  - Category: `unspecified-high`
  - Skills: []
  - Parallelization: Wave 3

- [x] 18. 编写集成测试

  **What to do**:
  - 测试完整流程：创建 → 消息 → 保存 → 恢复
  - 测试压缩功能
  
  **Recommended Agent Profile**:
  - Category: `unspecified-high`
  - Skills: []
  - Parallelization: Wave 3

---

### Wave Final: 验证

- [x] F1. 构建验证

  **What to do**:
  - `dotnet build src/Seeing.Session` → 0 errors
  - `dotnet build src/Seeing.Agent` → 0 errors
  
  **Recommended Agent Profile**:
  - Category: `quick`
  - Skills: []

- [x] F2. 测试验证

  **What to do**:
  - `dotnet test tests/Seeing.Session.Tests` → all pass
  - `dotnet test tests/Seeing.Agent.Tests` → all pass
  
  **Recommended Agent Profile**:
  - Category: `quick`
  - Skills: []

- [x] F3. Agent 包集成验证

  **What to do**:
  - 验证 SessionManager DI 正常注册
  - 验证 TaskTool/TodoWriteTool 正常工作
  
  **Recommended Agent Profile**:
  - Category: `unspecified-high`
  - Skills: []

- [x] F4. WebUI 适配验证

  **What to do**:
  - 更新 SessionStoreAdapter 使用 SessionData
  - 验证 WebUI 构建
  
  **Recommended Agent Profile**:
  - Category: `visual-engineering`
  - Skills: []

- [x] F5. API 变更文档

  **What to do**:
  - 记录 ISessionManager API 变更
  - 记录迁移指南
  
  **Recommended Agent Profile**:
  - Category: `writing`
  - Skills: []

---

## API 变更对照表

### ISessionManager

| 原方法 | 新方法 | 变更类型 |
|--------|--------|----------|
| CreateSessionAsync(agentId, agentName, workingDirectory) | Create(partitionId, agent) | 合并简化 |
| CreateSession(agentId, agentName) | ❌ 删除 | 移除同步版本 |
| GetSession(sessionId) | Get(id) | 重命名 |
| GetOrCreateSession(sessionId, agentId, agentName) | GetOrLoadAsync(id) | 重构 |
| DeleteSessionAsync(sessionId) | Delete(id) | 简化 |
| DeleteSession(sessionId) | ❌ 删除 | 移除同步版本 |
| AddMessageAsync(sessionId, message) | session.AddMessage(message) | 直接操作 |
| AddMessage(sessionId, message) | ❌ 删除 | 直接操作 |
| GetMessages(sessionId) | session.Messages | 直接访问 |
| SetContextAsync(sessionId, key, value) | session.SetContext(key, value) | 直接操作 |
| SetContext(sessionId, key, value) | ❌ 删除 | 直接操作 |
| GetContext<T>(sessionId, key) | session.GetContext<T>(key) | 直接操作 |
| GetActiveSessions() | List() | 重命名 |
| CleanupExpiredSessionsAsync(expiration) | CleanupAsync(expiration) | 简化 |
| CleanupExpiredSessions(expiration) | ❌ 删除 | 移除同步版本 |
| SetIdleAsync(sessionId) | ❌ 删除 | session.Status = Idle |
| SetErrorAsync(sessionId, error) | ❌ 删除 | session.Status = Error |
| CompactAsync(sessionId, cancellationToken) | Compress(id) | 简化 |

### SessionEntry → SessionData

| 原属性/方法 | 新属性/方法 | 变更 |
|-------------|-------------|------|
| SessionId | Id | 重命名 |
| CreatedAt | CreatedAt | 保持 |
| LastActiveAt | LastActiveAt | 保持 |
| ActiveAgent | Agent | 重命名 |
| WorkingDirectory | WorkingDirectory | 保持 |
| Messages (ConcurrentQueue) | Messages (List) | 类型变更 |
| Context (ConcurrentDictionary) | Context (Dictionary) | 类型变更 |
| AddMessage(message) | AddMessage(message) | 保持 |
| GetContextValue<T>(key) | GetContext<T>(key) | 重命名 |
| ClearMessages() | ClearMessages() | 保持 |

---

## 迁移指南

### 旧代码 → 新代码

```csharp
// === 旧代码 ===
var sessionManager = new SessionManager(hookManager);
var session = sessionManager.CreateSessionAsync("agent-1", "MyAgent");
sessionManager.AddMessageAsync(session.SessionId, message);
sessionManager.SetContextAsync(session.SessionId, "key", value);
await sessionManager.SaveSessionAsync(session.SessionId);

// === 新代码 ===
var sessionManager = new SessionManager(
    store: new FileSessionStore(),
    compressor: new SlidingWindowCompression(),
    hooks: hookManager);

var session = sessionManager.Create(
    partitionId: "agent-1",
    agent: new AgentMetadata { AgentId = "agent-1", AgentName = "MyAgent" });

session.AddMessage(message);
session.SetContext("key", value);
await sessionManager.SaveAsync(session.Id);
```

### DI 注册变更

```csharp
// === 旧注册 ===
services.AddSingleton<SessionManager>();
services.AddSingleton<ISessionManager>(sp => sp.GetRequiredService<SessionManager>());

// === 新注册 ===
services.AddSingleton<ISessionStore, FileSessionStore>();
services.AddSingleton<ICompressionStrategy, SlidingWindowCompression>();
services.AddSingleton<ISessionHookManager, SessionHookManager>();
services.AddSingleton<ISessionManager, SessionManager>();
```

---

## Commit Strategy

- **Wave 1**: `refactor(session): unify SessionData with SessionEntry`
- **Wave 2**: `refactor(session): simplify ISessionManager to 8 methods`
- **Wave 3**: `refactor(session): add ICompressionStrategy and update SessionManager`
- **Final**: `test(session): verify refactoring complete`

---

## Success Criteria

### 验证命令
```bash
dotnet build src/Seeing.Session     # 0 errors
dotnet build src/Seeing.Agent       # 0 errors
dotnet test tests/Seeing.Session.Tests  # all pass
dotnet test tests/Seeing.Agent.Tests    # all pass
dotnet build samples/Seeing.Agent.WebUI # 0 errors
```

### 最终检查
- [x] SessionData 包含 Messages 字段
- [x] SessionEntry 文件已删除
- [x] ISessionManager ≤ 10 方法
- [x] ICompressionStrategy 接口存在
- [x] SessionHookPoints 包含 MessageAdded/Compressed
- [x] SessionStatus 为 6 状态
- [x] 所有测试通过
- [x] Agent 包正常使用