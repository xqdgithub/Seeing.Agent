# Session 架构优化方案

## TL;DR

> **核心目标**: 解决 SessionData/SessionEntry 双重模型混乱，实现消息历史持久化，统一会话状态管理
>
> **关键改动**: 合并数据模型、增强 ISessionStore 接口、扩展 Hook 点、添加压缩策略抽象
>
> **预计工作量**: Medium（2-3天）
> **并行执行**: YES - 3 Waves
> **关键路径**: Wave 1（数据模型统一） → Wave 2（存储层增强） → Wave 3（管理器集成）

---

## 问题清单

| # | 问题 | 严重性 | 影响 |
|---|------|--------|------|
| 1 | SessionData vs SessionEntry 双重模型 | 高 | 消息历史无法持久化 |
| 2 | 消息历史无法持久化 | 高 | 应用重启丢失会话 |
| 3 | ISession 接口未被使用 | 中 | 接口与实现不匹配 |
| 4 | 接口返回具体类型 | 中 | 限制实现灵活性 |
| 5 | 压缩器无策略抽象 | 中 | 无法扩展压缩算法 |
| 6 | Hook 点设计混乱 | 中 | 语义不明确 |
| 7 | 状态模型不统一 | 中 | 三种不同表示 |
| 8 | SessionEntry 线程安全优化 | 低 | 清除消息性能 |
| 9 | SessionManager 缺少持久化集成 | 高 | 完全内存管理 |

---

## 优化方案

### 方案 A：渐进式优化（推荐）

**优点**: 低风险，向后兼容，分阶段实施
**缺点**: 需要更多时间

**阶段划分**:
- Phase 1: 核心数据模型统一（解决最严重问题）
- Phase 2: 存储层和接口增强
- Phase 3: 高级特性（压缩策略、Hook 扩展）

### 方案 B：重构式优化

**优点**: 一步到位，架构清晰
**缺点**: 高风险，可能破坏现有代码

---

## 采用方案 A（渐进式优化）

---

## Phase 1: 核心数据模型统一

### 目标
- 合并 SessionData 和 SessionEntry 为单一模型
- 实现消息历史持久化能力
- 保持向后兼容

### 改动详情

#### 1.1 增强 SessionData（替代 SessionEntry）

**当前 SessionData**:
```csharp
public class SessionData
{
    public string Id { get; set; }
    public string Title { get; set; }
    public string PartitionId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public AgentMetadata Agent { get; set; }
    public Dictionary<string, string> State { get; set; }
    public SessionStatus Status { get; set; }
}
```

**增强后 SessionData**:
```csharp
public class SessionData
{
    // === 基础信息 ===
    public string Id { get; set; }
    public string Title { get; set; }
    public string PartitionId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime LastActiveAt { get; set; }  // 新增
    
    // === Agent 信息 ===
    public AgentMetadata? Agent { get; set; }
    public string? WorkingDirectory { get; set; }  // 新增
    
    // === 状态 ===
    public SessionStatus Status { get; set; }
    public string? LastError { get; set; }  // 新增：最后错误信息
    
    // === 消息历史（核心新增） ===
    public List<SessionMessage> Messages { get; set; } = new();  // 新增
    
    // === 上下文数据 ===
    public Dictionary<string, object> Context { get; set; } = new();  // 改为 object
    
    // === 元数据 ===
    public Dictionary<string, string> Metadata { get; set; } = new();  // 新增：额外元数据
    
    // === 统计信息 ===
    public int TotalMessages { get; set; }  // 新增
    public int TotalTokens { get; set; }    // 新增
}
```

#### 1.2 保留 SessionEntry 作为 SessionData 的运行时包装

**设计理由**: 保持线程安全特性，但底层使用 SessionData

```csharp
public class SessionEntry
{
    private SessionData _data;
    private readonly object _lock = new();
    
    // 包装 SessionData
    public SessionEntry(SessionData data) => _data = data;
    
    // 基础属性（直接访问）
    public string SessionId => _data.Id;
    public DateTime CreatedAt => _data.CreatedAt;
    public SessionStatus Status => _data.Status;
    
    // 消息操作（线程安全）
    public void AddMessage(SessionMessage message) { lock (_lock) { _data.Messages.Add(message); } }
    public IReadOnlyList<SessionMessage> Messages => _data.Messages.ToList();  // 快照
    
    // 转换方法
    public SessionData ToSessionData() => _data;
    public static SessionEntry FromSessionData(SessionData data) => new SessionEntry(data);
}
```

---

## Phase 2: 存储层和接口增强

### 目标
- 增强 ISessionStore 支持完整会话数据
- SessionManager 支持可选持久化
- 统一接口返回类型

### 改动详情

#### 2.1 增强 ISessionStore 接口

**当前接口**:
```csharp
public interface ISessionStore
{
    Task SaveAsync(SessionData data);
    Task<SessionData?> LoadAsync(string sessionId);
    Task DeleteAsync(string sessionId);
    Task<IAsyncEnumerable<SessionData>> ListAsync();
    Task<IAsyncEnumerable<SessionData>> QueryAsync(string partitionId, string agentId);
}
```

**增强后接口**:
```csharp
public interface ISessionStore
{
    // === 基础操作 ===
    Task SaveAsync(SessionData data);
    Task<SessionData?> LoadAsync(string sessionId);
    Task DeleteAsync(string sessionId);
    Task<bool> ExistsAsync(string sessionId);  // 新增
    
    // === 批量操作 ===
    Task SaveAllAsync(IEnumerable<SessionData> data);
    Task<IAsyncEnumerable<SessionData>> LoadAllAsync();
    
    // === 查询操作 ===
    Task<IAsyncEnumerable<SessionData>> ListAsync();
    Task<IAsyncEnumerable<SessionData>> QueryAsync(string partitionId, string agentId);
    Task<IAsyncEnumerable<SessionData>> QueryByStatusAsync(SessionStatus status);  // 新增
    Task<IAsyncEnumerable<SessionData>> QueryByTimeRangeAsync(DateTime from, DateTime to);  // 新增
    
    // === 消息操作（新增） ===
    Task AppendMessageAsync(string sessionId, SessionMessage message);
    Task<IReadOnlyList<SessionMessage>> LoadMessagesAsync(string sessionId);
    Task ClearMessagesAsync(string sessionId);
    
    // === 统计操作（新增） ===
    Task<int> CountAsync();
    Task<int> CountByPartitionAsync(string partitionId);
}
```

#### 2.2 增强 SessionManager

**新增持久化模式**:
```csharp
public enum SessionPersistenceMode
{
    MemoryOnly,      // 仅内存（当前行为）
    Immediate,       // 立即持久化
    Deferred,        // 延迟持久化（定期保存）
    Hybrid           // 内存 + 定期同步到存储
}
```

**增强后 SessionManager**:
```csharp
public class SessionManager : ISessionManager
{
    private readonly ISessionStore? _store;
    private readonly SessionPersistenceMode _mode;
    private readonly TimeSpan _syncInterval;
    
    public SessionManager(
        SessionHookManager hookManager,
        ISessionStore? store = null,
        SessionPersistenceMode mode = SessionPersistenceMode.MemoryOnly,
        TimeSpan? syncInterval = null)
    {
        _store = store;
        _mode = mode;
        _syncInterval = syncInterval ?? TimeSpan.FromMinutes(5);
    }
    
    // 新增：从存储恢复会话
    public async Task<SessionEntry?> RestoreSessionAsync(string sessionId);
    
    // 新增：保存单个会话
    public async Task SaveSessionAsync(string sessionId);
    
    // 新增：保存所有会话
    public async Task SaveAllSessionsAsync();
    
    // 新增：定期同步（Hybrid 模式）
    public void StartPeriodicSync();
    public void StopPeriodicSync();
}
```

#### 2.3 统一接口返回类型

```csharp
public interface ISessionManager
{
    // 返回接口而非具体类型
    ISession CreateSessionAsync(string? agentId = null, ...);
    ISession? GetSession(string sessionId);
    ISession GetOrCreateSession(string? sessionId, ...);
    
    // 或使用增强后的 SessionEntry
    SessionEntry CreateSessionAsync(...);  // 保持向后兼容
}
```

---

## Phase 3: 高级特性增强

### 目标
- 扩展 Hook 点语义
- 添加压缩策略抽象
- 统一状态模型

### 改动详情

#### 3.1 扩展 Hook 点

**当前 Hook 点**:
```csharp
public static class SessionHookPoints
{
    public const string Created = "session.created";
    public const string Destroyed = "session.destroyed";
    public const string Saving = "session.saving";
    public const string Saved = "session.saved";
}
```

**扩展后 Hook 点**:
```csharp
public static class SessionHookPoints
{
    // === 生命周期 ===
    public const string Created = "session.created";
    public const string Destroyed = "session.destroyed";
    public const string Saving = "session.saving";
    public const string Saved = "session.saved";
    public const string Loaded = "session.loaded";        // 新增
    public const string Restored = "session.restored";    // 新增
    
    // === 消息操作 ===
    public const string MessageAdded = "session.message_added";     // 新增
    public const string MessageRemoved = "session.message_removed"; // 新增
    public const string MessagesCleared = "session.messages_cleared"; // 新增
    
    // === 状态变化 ===
    public const string StatusChanged = "session.status_changed";   // 新增
    public const string IdleEntered = "session.idle_entered";       // 新增
    public const string ErrorOccurred = "session.error_occurred";   // 新增
    
    // === 上下文操作 ===
    public const string ContextUpdated = "session.context_updated"; // 新增
    
    // === 压缩操作 ===
    public const string Compacting = "session.compacting";          // 新增
    public const string Compacted = "session.compacted";           // 新增
}
```

#### 3.2 添加压缩策略抽象

```csharp
public interface IMessageCompressionStrategy
{
    string Name { get; }
    string Description { get; }
    
    Task<List<SessionMessage>> CompressAsync(
        IReadOnlyList<SessionMessage> messages, 
        CompressionOptions options,
        CancellationToken cancellationToken = default);
    
    int EstimateRetainedCount(int messageCount, CompressionOptions options);
}

public class CompressionOptions
{
    public int KeepLastN { get; set; } = 20;
    public bool KeepSystemMessage { get; set; } = true;
    public bool KeepErrorMessages { get; set; } = true;
    public int? MaxTokens { get; set; }
}

// 实现
public class SlidingWindowStrategy : IMessageCompressionStrategy { ... }
public class ImportanceBasedStrategy : IMessageCompressionStrategy { ... }
public class TokenLimitStrategy : IMessageCompressionStrategy { ... }
```

#### 3.3 统一状态模型

```csharp
public enum SessionStatus
{
    Created = 0,      // 新创建
    Running = 1,      // 正在执行
    Paused = 2,       // 已暂停
    Idle = 3,         // 空闲等待（新增）
    Completed = 4,    // 已完成
    Error = 5,        // 错误状态（新增）
    Destroyed = 6     // 已销毁（新增）
}

// IExecutionState 映射
public interface IExecutionState
{
    bool IsExecuting => Status == SessionStatus.Running;
    bool IsPaused => Status == SessionStatus.Paused;
    SessionStatus Status { get; }
}
```

---

## 执行策略

### Wave 1: 核心数据模型（立即开始）

```
Wave 1（并行执行）:
├── Task 1: 增强 SessionData 模型（添加 Messages/Context）
├── Task 2: 重构 SessionEntry 为 SessionData 包装器
├── Task 3: 添加 SessionData ↔ SessionEntry 转换方法
├── Task 4: 更新 SessionStatus 枚举
├── Task 5: 更新 SessionMessage 序列化支持
└── Task 6: 编写单元测试验证新模型
```

### Wave 2: 存储层增强

```
Wave 2（依赖 Wave 1，并行执行）:
├── Task 7: 增强 ISessionStore 接口（消息操作）
├── Task 8: 实现 FileSessionStore 消息持久化
├── Task 9: 实现 InMemorySessionStore 消息操作
├── Task 10: 添加 SessionPersistenceMode 枚举
├── Task 11: 增强 SessionManager 持久化支持
└── Task 12: 编写存储层集成测试
```

### Wave 3: 高级特性

```
Wave 3（依赖 Wave 2，并行执行）:
├── Task 13: 扩展 SessionHookPoints 常量
├── Task 14: 更新 SessionManager Hook 触发逻辑
├── Task 15: 创建 IMessageCompressionStrategy 接口
├── Task 16: 实现 SlidingWindowStrategy
├── Task 17: 实现 TokenLimitStrategy（可选）
├── Task 18: 更新 SessionCompressor 使用策略
└── Task 19: 编写 Hook 和压缩测试
```

### Wave Final: 验证

```
Wave Final:
├── Task F1: 计划合规审计（oracle）
├── Task F2: 代码质量审查（unspecified-high）
├── Task F3: 集成测试执行（unspecified-high）
├── Task F4: 范围一致性检查（deep）
```

---

## TODOs

- [ ] 1. 增强 SessionData 模型 - 添加 Messages/Context/WorkingDirectory

  **What to do**:
  - 修改 `SessionData.cs`，添加消息历史和上下文字段
  - 修改 `Context` 类型从 `Dictionary<string, string>` 到 `Dictionary<string, object>`
  - 添加 `LastActiveAt`, `WorkingDirectory`, `LastError`, `Metadata` 字段

  **Recommended Agent Profile**:
  - Category: `quick`
  - Skills: []

  **Parallelization**: Wave 1 (with Tasks 2-6)

- [ ] 2. 重构 SessionEntry 为 SessionData 包装器

  **What to do**:
  - 修改 `SessionEntry.cs`，内部使用 `SessionData` 存储
  - 添加 `ToSessionData()` 和 `FromSessionData()` 方法
  - 保持线程安全特性，使用锁保护 `_data`

  **Recommended Agent Profile**:
  - Category: `quick`
  - Skills: []

  **Parallelization**: Wave 1

- [ ] 3. 添加转换方法

  **What to do**:
  - `SessionEntry.ToSessionData()` 返回内部数据副本
  - `SessionEntry.FromSessionData(SessionData)` 创建新实例
  - 确保 JSON 序列化兼容

  **Recommended Agent Profile**:
  - Category: `quick`
  - Skills: []

  **Parallelization**: Wave 1

- [ ] 4. 更新 SessionStatus 枚举

  **What to do**:
  - 添加 `Idle = 3`, `Error = 5`, `Destroyed = 6`
  - 更新 XML 注释说明每个状态含义

  **Recommended Agent Profile**:
  - Category: `quick`
  - Skills: []

  **Parallelization**: Wave 1

- [ ] 5. 更新 SessionMessage 序列化

  **What to do**:
  - 验证新增字段正确序列化为 JSON
  - 添加 JSON 序列化测试

  **Recommended Agent Profile**:
  - Category: `quick`
  - Skills: []

  **Parallelization**: Wave 1

- [ ] 6. 编写单元测试验证新模型

  **What to do**:
  - 测试 SessionData 消息操作
  - 测试 SessionEntry ↔ SessionData 转换
  - 测试 JSON 序列化/反序列化

  **Recommended Agent Profile**:
  - Category: `quick`
  - Skills: []

  **Parallelization**: Wave 1

---

- [ ] 7. 增强 ISessionStore 接口

  **What to do**:
  - 添加 `ExistsAsync`, `QueryByStatusAsync`, `QueryByTimeRangeAsync`
  - 添加消息操作: `AppendMessageAsync`, `LoadMessagesAsync`, `ClearMessagesAsync`
  - 添加统计操作: `CountAsync`, `CountByPartitionAsync`

  **Recommended Agent Profile**:
  - Category: `quick`
  - Skills: []

  **Parallelization**: Wave 2

- [ ] 8. 实现 FileSessionStore 消息持久化

  **What to do**:
  - 修改 `FileSessionStore` 保存完整 `SessionData`（含消息）
  - 实现消息追加优化（不重写整个文件）
  - 添加消息加载方法

  **Recommended Agent Profile**:
  - Category: `unspecified-high`
  - Skills: []

  **Parallelization**: Wave 2

- [ ] 9. 实现 InMemorySessionStore 消息操作

  **What to do**:
  - 实现新接口方法
  - 使用 ConcurrentDictionary 保证线程安全

  **Recommended Agent Profile**:
  - Category: `quick`
  - Skills: []

  **Parallelization**: Wave 2

- [ ] 10. 添加 SessionPersistenceMode 枚举

  **What to do**:
  - 定义: `MemoryOnly`, `Immediate`, `Deferred`, `Hybrid`
  - 添加 XML 注释说明每种模式用途

  **Recommended Agent Profile**:
  - Category: `quick`
  - Skills: []

  **Parallelization**: Wave 2

- [ ] 11. 增强 SessionManager 持久化支持

  **What to do**:
  - 添加 `ISessionStore` 和 `SessionPersistenceMode` 参数
  - 实现 `RestoreSessionAsync`, `SaveSessionAsync`
  - 实现 Hybrid 模式定期同步

  **Recommended Agent Profile**:
  - Category: `deep`
  - Skills: []

  **Parallelization**: Wave 2

- [ ] 12. 编写存储层集成测试

  **What to do**:
  - 测试 FileSessionStore 完整会话持久化
  - 测试消息追加和加载
  - 测试 SessionManager 持久化模式

  **Recommended Agent Profile**:
  - Category: `unspecified-high`
  - Skills: []

  **Parallelization**: Wave 2

---

- [ ] 13. 扩展 SessionHookPoints 常量

  **What to do**:
  - 添加新 Hook 点: `MessageAdded`, `StatusChanged`, `ErrorOccurred`, 等
  - 更新 XML 注释

  **Recommended Agent Profile**:
  - Category: `quick`
  - Skills: []

  **Parallelization**: Wave 3

- [ ] 14. 更新 SessionManager Hook 触发逻辑

  **What to do**:
  - 消息添加触发 `MessageAdded`
  - 状态变化触发 `StatusChanged`
  - 错误触发 `ErrorOccurred`
  - 移除对 `Saved` 的滥用

  **Recommended Agent Profile**:
  - Category: `quick`
  - Skills: []

  **Parallelization**: Wave 3

- [ ] 15. 创建 IMessageCompressionStrategy 接口

  **What to do**:
  - 定义接口: `Name`, `Description`, `CompressAsync`
  - 创建 `CompressionOptions` 配置类

  **Recommended Agent Profile**:
  - Category: `quick`
  - Skills: []

  **Parallelization**: Wave 3

- [ ] 16. 实现 SlidingWindowStrategy

  **What to do**:
  - 实现滑动窗口压缩算法
  - 支持配置: `KeepLastN`, `KeepSystemMessage`

  **Recommended Agent Profile**:
  - Category: `quick`
  - Skills: []

  **Parallelization**: Wave 3

- [ ] 17. 实现 TokenLimitStrategy（可选）

  **What to do**:
  - 基于 Token 估算压缩
  - 超出限制时触发压缩

  **Recommended Agent Profile**:
  - Category: `unspecified-high`
  - Skills: []

  **Parallelization**: Wave 3

- [ ] 18. 更新 SessionCompressor 使用策略

  **What to do**:
  - 重构为策略模式
  - 默认使用 SlidingWindowStrategy

  **Recommended Agent Profile**:
  - Category: `quick`
  - Skills: []

  **Parallelization**: Wave 3

- [ ] 19. 编写 Hook 和压缩测试

  **What to do**:
  - 测试新 Hook 点触发
  - 测试压缩策略效果

  **Recommended Agent Profile**:
  - Category: `unspecified-high`
  - Skills: []

  **Parallelization**: Wave 3

---

## Final Verification Wave

- [ ] F1. 计划合规审计（oracle）
- [ ] F2. 代码质量审查（unspecified-high）
- [ ] F3. 集成测试执行（unspecified-high）
- [ ] F4. 范围一致性检查（deep）

---

## 向后兼容策略

### 保持兼容的部分

1. **SessionEntry 公开 API 不变**:
   - `SessionId`, `Messages`, `AddMessage()` 等方法保持不变
   - 内部实现改为包装 SessionData

2. **SessionManager 接口不变**:
   - 默认行为保持 MemoryOnly 模式
   - 新功能通过可选参数提供

3. **ISessionStore 基础方法不变**:
   - `SaveAsync`, `LoadAsync`, `DeleteAsync` 保持签名
   - 新方法为扩展方法

### 迁移指南

```csharp
// 旧代码（兼容）
var session = sessionManager.CreateSession("agent-1");
session.AddMessage(SessionMessage.UserMessage("Hello"));

// 新代码（持久化）
var sessionManager = new SessionManager(
    hookManager,
    new FileSessionStore(),
    SessionPersistenceMode.Hybrid);

var session = sessionManager.CreateSession("agent-1");
session.AddMessage(SessionMessage.UserMessage("Hello"));
// 自动定期保存，或手动调用
await sessionManager.SaveSessionAsync(session.SessionId);
```

---

## Commit Strategy

- **Wave 1**: `refactor(session): unify SessionData and SessionEntry models`
- **Wave 2**: `feat(session): add persistence support to SessionManager`
- **Wave 3**: `feat(session): add compression strategy and extended hooks`
- **Final**: `test(session): verify optimization completeness`

---

## Success Criteria

### 验证命令
```bash
dotnet build src/Seeing.Session      # 0 errors
dotnet test tests/Seeing.Session.Tests  # all pass
dotnet test tests/Seeing.Agent.Tests    # all pass (向后兼容)
```

### 最终检查
- [ ] SessionData 包含消息历史
- [ ] FileSessionStore 保存完整会话数据
- [ ] SessionManager 支持可选持久化
- [ ] Hook 点语义明确
- [ ] 压缩策略可扩展
- [ ] 向后兼容验证通过