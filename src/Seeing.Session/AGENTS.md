# Seeing.Session - 独立会话管理包

**父包:** Seeing.Agent（可独立使用）
**用途:** AI Agent 会话生命周期管理，无主库依赖

---

## STRUCTURE

```
Seeing.Session/
├── Core/                  # 核心接口与实现
│   ├── ISession.cs        # 会话契约
│   ├── ISessionManager.cs # 管理器接口
│   ├── ISessionFactory.cs # 工厂接口
│   ├── ISessionLifecycle.cs # 生命周期钩子
│   ├── Session.cs         # 会话实体
│   └── SessionData.cs     # 数据容器
│
├── Management/
│   └── SessionManager.cs  # 主管理器实现
│
├── Storage/
│   ├── ISessionStore.cs   # 存储抽象
│   ├── FileSessionStore.cs # 文件持久化
│   └── InMemorySessionStore.cs # 内存存储
│
├── Compression/
│   ├── ICompressionStrategy.cs # 压缩策略接口
│   ├── SessionCompressor.cs # 压缩执行器
│   └── SlidingWindowCompression.cs # 滑动窗口策略
│
├── Hooks/
│   ├── ISessionHook.cs    # 会话钩子接口
│   ├── SessionHookManager.cs # 钩子管理
│   └── SessionHookPoints.cs # 钩子点常量
│
└── Execution/
│   ├── IExecutionState.cs # 执行状态接口
│   └ ExecutionStateManager.cs # 状态管理
```

---

## WHERE TO LOOK

| 任务 | 文件 | 关键成员 |
|------|------|----------|
| 创建会话 | `Management/SessionManager.cs` | `CreateAsync()` |
| 加载会话 | `Storage/FileSessionStore.cs` | `LoadAsync()` |
| 压缩历史 | `Compression/SessionCompressor.cs` | `CompressAsync()` |
| 添加钩子 | `Hooks/SessionHookManager.cs` | `RegisterHook()` |
| 生命周期 | `Core/ISessionLifecycle.cs` | `OnCreated/OnUpdated/OnDeleted` |

---

## CONVENTIONS

### Hook 点（Session 专属）
```csharp
SessionHookPoints.Created    // "session.created"
SessionHookPoints.Updated    // "session.updated"
SessionHookPoints.Deleted    // "session.deleted"
SessionHookPoints.Compacting // "session.compacting"
SessionHookPoints.Idle       // "session.idle"
SessionHookPoints.Error      // "session.error"
```

### 压缩策略
- **SlidingWindow**: 保留最近 N 条消息 + 摘要历史
- **TTL**: 会话数据默认保留 7 天

---

## ANTI-PATTERNS

| 禁止 | 原因 |
|------|------|
| 直接修改 SessionData | 使用 SessionManager API |
| 绕过 ISessionStore | 破坏持久化一致性 |
| 钩子中阻塞操作 | 影响会话生命周期性能 |

---

## NOTES

- **依赖**: 仅 `Microsoft.Extensions.Logging.Abstractions`
- **可独立安装**: `dotnet add package Seeing.Session`
- **与主库集成**: 通过 DI 自动注册