# 会话工具模块与通用会话组设计

**日期：** 2026-09-18
**状态：** ✅ **已定稿（v7，子 Agent 终审通过并签字）**
**范围：** 全栈（原语层 + 契约层 + 脊柱层 + 新能力包 + WebUI）
**相关：** [模块化架构设计](2026-09-08-modular-architecture-design.md)、`docs/architecture/01–06`

---

## 1. 背景与目标

当前 Agent 只能在一个会话内工作：无法检索/读取其它会话、无法主动裁剪本会话上下文、无法把任务交接给另一个干净会话继续。同时 WebUI 的多会话窗口逻辑（`ConferenceRegistry` + `SessionWindow` 的 Full/Summary）**硬绑定子代理（SubAgent）**。

本设计做两件事：

1. **新增 `session.tools` 能力模块**：`session_list` / `session_search` / `session_read` / `session_trim` / `session_handoff`。
2. **引入通用会话组（`SessionGroup`）**，作为会话间**关系的唯一权威**；WebUI 窗口展示与切换改为**组驱动**，与关系类型解耦，天然支持多 Agent 并行执行。

### 1.1 核心重构原则（禁止补丁）

**关系（父子 / 分支 / 交接）只存在一处：`SessionGroup`；所有会产生关系的操作只经过一个入口：`ISessionGroupManager`。**

- `SessionManager` 降级为**低层会话 CRUD/持久化**，**不具备任何关系语义**（移除 `ForkAsync`、`CreateChildAsync`、`ListChildrenAsync`、`ListRootsAsync`、`LoadChildrenFromStorageAsync`）。
- `SessionData.Kind` **去除关系语义**：`SessionKind` 仅 `Root | SubAgent` 两个**内在行为类型**；删除 `Kind.Fork`（分支是关系，由组承载）。子代理是 Agent 派生任务，其"只读/权限快照"是内在行为，保留 `SubAgent`。
- **不存在旁路**：任何创建带关系的会话的操作，只能调 `ISessionGroupManager`。`/fork` 命令、`BranchSessionAsync`、TaskTool、handoff、trim 备份全部走组管理器。

**前提：** 开发期，允许重构，**不保留向后兼容**。

### 1.2 非目标
- 不改 LLM Provider / 工具装饰器 / 权限引擎决策链。
- 不做跨 Partition 会话共享。
- 不做"交接给已有会话"（本期仅新建干净会话）；组模型已预留。
- **`ForkSessionAsync` 不保留 `atMessageId`**（现 `ISessionManager.ForkAsync` 支持按消息截断；生产无调用方，仅测试使用）；如需按点分支后续单开。
- 移除 `ISessionManager.ForkAsync` 的 `atMessageId`（按消息点分支）能力：`ForkSessionAsync` 仅整会话分支；该能力在生产无调用方（仅测试 `SessionForker` 用）。

---

## 2. 术语

| 术语 | 含义 |
|---|---|
| 活跃消息 | `SessionData.GetActiveMessages()`（最后一条 `IsSummary` 摘要及其之后） |
| 锚点会话 | `SessionGroup.AnchorSessionId`，组的路由/列表入口 |
| active 会话 | `SessionGroup.ActiveSessionId`，组内当前主窗（Full） |
| 关联会话 | 与当前会话同处一个 `SessionGroup` 的成员 |
| 交接 | 把后续工作移交到新建干净 Root 执行，当前执行回合结束 |

---

## 3. 分层与合规约束

| 层 | 改动 | 合规要求 |
|---|---|---|
| 原语 `src/primitives/Seeing.Session/` | 组实体/存储/管理器；`SessionManager` 降级 | 零主库依赖，仅 BCL + Logging + DI |
| 契约 `src/abstractions/Seeing.Agent.Abstractions/` | 终止指令、组事件契约与事件总线 | 只放契约，零实现 |
| 脊柱 `src/spine/Seeing.Agent.Core/` | `AgentExecutor` 消费指令；组事件桥（HostedService）；组 store/manager 注册与重定位 | 不引能力包 |
| 能力 `src/capabilities/Seeing.Agent.Tools.Session/` | 新包（id `session.tools`） | 只依赖 Abstractions + Seeing.Session + Tools.Support（`TokenBudget`/`Acp` 先例） |
| WebUI `samples/Seeing.Agent.WebUI/` | 组驱动窗口 | 经 Abstractions/原语端口访问组 |

**能力包可注入端口（已核验）：** `ISessionManager`、`ISessionGroupManager`（新增，均在 `primitives/Seeing.Session/Core/`）、`IExecutionSubmitter`/`IExecutionStatusProvider`（`Abstractions/Execution/`）、`IExecutionEventPublisher`（`Abstractions/Events/`）、`IPermissionAuthorizerFactory`（`Abstractions/Permissions/`）。

> `ICompressionService` 本期不使用；它仍被 `TokenBudget` 消费，**不可移除**。
> `IWorkspaceProvider` 在 Abstractions，原语层**不可**引用；组存储用 `baseDirectory` 构造（同 `FileSessionStore.cs:35`），由 **Core** 注入。

---

## 4. 数据模型与关系权威

### 4.1 实体（`Seeing.Session.Core`）

```csharp
public enum SessionRelation
{
    None = 0,               // 锚点成员（无外部关系）
    Child = 1,              // 子 Agent / Task 会话
    Fork = 2,               // 分支会话（含 trim 备份）
    HandoffSuccessor = 3    // 交接后继（前驱 = 反查 ParentSessionId）
}

public enum SessionKind { Root = 0, SubAgent = 2 }   // 内在行为类型（无关系语义）；SubAgent 保留数值 2，删除 Fork 只留空档，避免存量 JSON 数值枚举重编号失效

public sealed class SessionGroup
{
    public string Id { get; set; } = string.Empty;          // grp_yyyyMMddHHmmss_xxxxxxxx
    public string PartitionId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string AnchorSessionId { get; set; } = string.Empty;
    public string? ActiveSessionId { get; set; }            // null = Anchor
    public List<SessionGroupMember> Members { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public long Version { get; set; }                       // 每次变更 +1（组锁内）；用于订阅方丢弃乱序快照

    public string ResolveActiveId() =>
        (ActiveSessionId is not null && Members.Any(m => m.SessionId == ActiveSessionId))
            ? ActiveSessionId : AnchorSessionId;
}

public sealed class SessionGroupMember
{
    public string SessionId { get; set; } = string.Empty;
    public SessionRelation Relation { get; set; }
    public string? ParentSessionId { get; set; }            // 谱系父指针（Child/Fork/Handoff 均写源；锚点可 null）
    public string? Label { get; set; }                      // 原 ForkLabel 语义
    public bool IsAnchor { get; set; }
    public int Order { get; set; }
}

public sealed class SessionGroupChangedEventArgs : EventArgs
{
    public required SessionGroup Group { get; init; }
}
```

### 4.2 `SessionData` 变更（去关系语义）

| 操作 | 字段 |
|---|---|
| **新增** | `string? GroupId`（由组管理器唯一写入） |
| **移除** | `ParentSessionId`、`ForkLabel`（关系迁入组） |
| **变更** | `SessionKind` 去 `Fork`（仅 `Root=0`/`SubAgent=2`，**保留 SubAgent 数值 2** 以免存量 JSON 数值枚举重编号失效）；分支由 `Relation=Fork` 承载 |
| **保留** | `IsArchived`、`ArchivedAt`、`PermissionSnapshot`、`Selected*`、消息、`Context`、`Metadata` 等 |

**`Kind` 的用途（仅内在行为，全部保留）：**
- `AgentExecutor.ResolvePolicy`（`AgentExecutor.cs:974`）SubAgent 合并权限快照
- `TaskTool.cs:112` / `TaskStatusTool.cs:131` 任务归属校验
- `ExecutionJobService.cs:1268,1290` 执行归类
- `SessionTitleService.cs:96` 标题生成策略

> 关系/列表/展示一律以组为准。

### 4.3 依赖方向与"单一入口"（解决 DI 环 + 消除旁路）

```
SessionManager（低层会话 CRUD/持久化；无关系语义）
   ↑ 依赖
SessionGroupManager（关系唯一权威：创建/分支/子会话/交接/备份/删除级联/查询）
   ├─ 依赖 ISessionManager（写会话、写 GroupId）
   ├─ 依赖 ISessionGroupStore（组落盘）
   └─ 使用 SessionForker（纯消息复制引擎，不碰组）

SessionManager / SessionForker ——不依赖——> ISessionGroupManager
```

- `ISessionManager` 移除：`ForkAsync`、`CreateChildAsync`、`ListChildrenAsync`、`ListRootsAsync`、`LoadChildrenFromStorageAsync`。
- `SessionForker` 保留为**纯复制引擎**（复制消息、产出新 `SessionData`），由 `SessionGroupManager` 调用；不再自行设置 `Kind`（由组管理器按场景设定）。
- `SessionGroupManager` 单向依赖 `ISessionManager` + `ISessionGroupStore` → **无 DI 环**。

**唯一入口清单（所有关系操作的调用点，禁止旁路）：**
| 操作 | 入口 |
|---|---|
| 新建根会话（独立组） | `ISessionGroupManager.CreateRootAsync` |
| 新建子代理会话 | `ISessionGroupManager.CreateChildAsync` |
| 分支（Root→同组 Fork；SubAgent→独立新组） | `ISessionGroupManager.ForkSessionAsync`（按 `Kind` 分派） |
| trim 备份 | `ISessionGroupManager.CreateBackupForkAsync`（**始终入当前组**） |
| 交接后继 | `ISessionGroupManager.CreateHandoffSuccessorAsync` |
| 删除会话（含级联） | `ISessionGroupManager.RemoveSessionAsync` |

调用方归位：`ChatOrchestrator`（`CreateSessionAsync:146-186` 委托 `CreateRootAsync`、`BranchSessionAsync:222` 委托 `ForkSessionAsync`、`DeleteSessionAsync:189-207` 委托 `RemoveSessionAsync`）、`SessionCommands`(`/fork`)、`TaskTool`、handoff 工具、WebUI（`SessionWindow.razor:1305-1315` 清空流程）。

- **非关系创建（懒物化，非旁路）**：`AcpTool`、`GatewaySessionResolver`、`GatewayScheduleDispatcher`、`ScheduleDispatchers`、`AcpSessionStore`、`ExecutionJobService` 等处经 `ISessionManager.Create`/`EnsureSessionAsync` 直接建会话，不产生关系；其组由 §4.5.2/§4.5.3 物化规则补建，属**唯一入口之外的合法空组创建**。
- **`SessionForker` 注册**：`SessionForker` 进入 DI（`ILogger<SessionForker>` + `ISessionManager`），由 `SessionGroupManager` 构造注入；`SessionManager` 自身不再构造 forker（现 Core `ServiceCollectionExtensions.cs:454-462` 省略 forker、`SessionServiceExtensions.cs:65` 显式 `forker: null`，两处一并清理）。
- **两级锁（防双组 + 防组内丢更新）**：`SessionGroupManager` 维护 `ConcurrentDictionary<sessionId, SemaphoreSlim>`（会话锁）与 `ConcurrentDictionary<groupId, SemaphoreSlim>`（组锁）。
  - **会话→组指派**（CreateRoot/CreateChild/Fork/Backup/Handoff/Ensure 物化）：持**会话锁** → 建组/取组 → 回写 `GroupId` → 登记缓存；**锁内复检 `GroupId`** 幂等。跨两级时锁序恒为 `会话锁 → 组锁`。
  - **组内成员变更**（AddMember/RemoveMember/SetActive/RemoveSession 级联）：持**组锁**执行 read-modify-write；`Order` 在组锁内递增。
  - **锁顺序恒为 `会话锁 → 组锁`**（反向禁止），跨组批量操作按 `groupId` 升序取锁，防死锁。
  - **`Changed` 在两级锁之外触发**。

### 4.4 存储与端口

```csharp
public interface ISessionGroupStore
{
    Task<SessionGroup?> LoadAsync(string groupId);
    Task<SessionGroup?> FindBySessionAsync(string sessionId);   // 冷缓存：按成员反查
    Task SaveAsync(SessionGroup group);
    Task DeleteAsync(string groupId);
    IAsyncEnumerable<SessionGroup> ListAsync();
}

public interface ISessionGroupManager
{
    // 关系创建（唯一入口）
    Task<SessionData> CreateRootAsync(string? partitionId, string? agent, string? scenario, string? title, string? workingDirectory, CancellationToken ct = default);
    Task<SessionData> CreateChildAsync(string parentId, string agentName, string title,
        IReadOnlyList<SessionPermissionRule> snapshot, string? scenario, CancellationToken ct = default);
    Task<SessionData> ForkSessionAsync(string sessionId, string? title, CancellationToken ct = default);
    Task<SessionData> CreateBackupForkAsync(string sessionId, string label, CancellationToken ct = default);
    Task<SessionData> CreateHandoffSuccessorAsync(string sourceSessionId, string? agent, string? title, string? scenario, CancellationToken ct = default);

    // 组与成员
    Task<SessionGroup> EnsureForSessionAsync(string sessionId, CancellationToken ct = default);
    Task<SessionGroup?> GetGroupAsync(string groupId, CancellationToken ct = default);
    Task<SessionGroup?> GetGroupForSessionAsync(string sessionId, CancellationToken ct = default);
    Task AddMemberAsync(string groupId, SessionGroupMember member, CancellationToken ct = default);
    Task RemoveMemberAsync(string groupId, string sessionId, CancellationToken ct = default);
    Task SetActiveAsync(string groupId, string sessionId, CancellationToken ct = default);
    Task RemoveSessionAsync(string sessionId, CancellationToken ct = default);   // 级联删除

    // 查询
    Task<string?> GetParentAsync(string sessionId, CancellationToken ct = default);   // 返回成员 ParentSessionId（谱系父指针；含 Fork 的源）
    Task<IReadOnlyList<SessionData>> ListChildrenAsync(string parentId, CancellationToken ct = default);   // 谓词 = !IsAnchor && Relation==Child && ParentSessionId==parentId；含冷存储兜底
    Task<IReadOnlyList<SessionGroupMember>> ListMembersAsync(string groupId, CancellationToken ct = default);
    Task<IReadOnlyList<SessionData>> ListAnchorsAsync(string? partitionId = null, CancellationToken ct = default);  // 根列表

    event EventHandler<SessionGroupChangedEventArgs>? Changed;   // 锁外触发
}
```

- `FileSessionGroupStore(string? baseDirectory = null)`：默认 `~/.seeing/session-groups`；实现 `IRelocatableSessionGroupStore.SetBaseDirectory`（Core 注入 `{ProjectSeeingDirectory}/session-groups`，与会话存储 `{ProjectSeeingDirectory}/sessions` 同级，见 `ServiceCollectionExtensions.cs:443`）；写入加锁；JSON 与 session 存储同风格。
- `Order` 于锁内递增；`Changed` 在锁外触发。
- `ISessionGroupManager` 内部维护**内存组缓存**（`ConcurrentDictionary<groupId, SessionGroup>` + `sessionId→groupId` 索引），避免 `ListAnchorsAsync`/`ListChildrenAsync` 每次全量扫盘；写穿透更新缓存。

### 4.5 生命周期规则（闭环）

1. **单写者**：`SessionData.GroupId` 仅由 `ISessionGroupManager` 经 `UpdateSessionAsync` 写。
2. **必归属（原子）**：所有创建入口（§4.3）都产出一个组。会话创建 + 组创建 + `GroupId` 回写在**同一会话锁内**完成：先建组并 `SaveAsync`，再回写 `GroupId` 并 `SaveAsync`，最后登记缓存；`EnsureForSessionAsync` 在锁内复检 `GroupId`，不存在才建组（幂等）。锚点成员的 `Relation=None`。
3. **根列表物化**：`ListAnchorsAsync` 对 Partition 内 `GroupId` 为空的会话逐个经 `EnsureForSessionAsync`（同锁）物化单成员组，再返回锚点集合；依赖组缓存避免全量扫盘。
4. **Child**：`CreateChildAsync` → 父组，`Relation=Child, ParentSessionId=parent`。
5. **Fork**：`ForkSessionAsync` 按源 `Kind` 分派：
   - 源 `Root` → 源组，`Relation=Fork, ParentSessionId=源`；
   - 源 `SubAgent` → **独立新组**：新会话 `Kind=Root`（分离后为可编辑普通会话）、`Relation=None`（锚点）、`ParentSessionId=源` 仅记谱系。
   > `ParentSessionId` 为**谱系指针**（Child/Fork/Handoff 均写源）；`ListChildrenAsync` 只认 `Relation==Child`，UI 的 `ShowReturnToParent` 只认 `IsChild`，故 Fork 不会误显"返回父会话"。
6. **备份**：`CreateBackupForkAsync` **始终**加入源会话所在组，`Relation=Fork`（与源 `Kind` 无关）。
7. **交接**：`CreateHandoffSuccessorAsync` → 源组，`Relation=HandoffSuccessor, ParentSessionId=源` → `SetActiveAsync(目标)`。
8. **Order / Title**：成员按入组顺序递增；`Group.Title` 取锚点标题，锚点变更重算。
9. **归档 / 锚点提升迁移**：`IsArchived` 成员保留在组但不入窗，**`ListAnchorsAsync` 过滤 `IsArchived` 锚点**（与旧 `ListRootsAsync` 一致）；锚点归档或被删时，按 `Order` 提升下一成员（**优先跳过 `Relation=Fork` 的备份成员**，仅当无非 Fork 成员时提升 Fork），同步 `IsAnchor=true`、其 `Relation=None`，并**重算 `Group.Title` 为提升者标题**；原谱系 `ParentSessionId` 保留。
10. **删除级联**：`RemoveSessionAsync(sessionId)`：
    - 递归移除该会话下 `Relation=Child && ParentSessionId=sessionId` 的成员会话（及其子树）；
    - 从组移除成员；若删的是 active → `ActiveSessionId=null` 回退锚点；若删的是锚点 → 按 `Order` 提升（同规则 9）；组空 → 删除组并**广播一次 `Version` 递增的组已删除事件**（`Members 为空`），供按 groupId 订阅的 `SessionWindowRegistry` 收敛窗口/退订。
    - **不含取消在途执行**（原语层无执行端口）；取消由调用方（`ChatOrchestrator.DeleteSessionAsync` / `SessionWindow` 清空）在调用前经 `IExecutionSubmitter.CancelBySessionAsync` 完成。
    > **语义变化（明示）**：`ExecutionJobService.cs:307` 的级联取消由原"`ParentSessionId` 匹配"收窄为 `Relation==Child`；Fork 成员的在途执行**不再**随源会话取消/删除级联取消（符合"分支独立"语义）。
11. **关系枚举方向**：向下（子）用 `ListChildrenAsync`（`!IsAnchor && Relation==Child`）；向上（谱系父）用 `GetParentAsync`；全组用 `ListMembersAsync`。
12. **不变量**：**非锚点成员**满足 `Kind==SubAgent ⟺ Relation==Child`（仅 `CreateChildAsync` 同时写入）；锚点成员恒 `Relation=None`。新增断言与测试，禁止其它路径写两字段。
    > **修订（2026-09-18）**：本约束已放宽——锚点改由 `IsAnchor` 判定，其 `Relation ∈ { None, HandoffSuccessor }`（`Fork` 兜底当选归一为 `None`；仅剩 `Child` 的退化态保持 `Child`；`SubAgent ⟺ Child` 不变）。详见 `docs/superpowers/specs/2026-09-18-session-relation-tree-and-display-design.md` §2.3、§5。

### 4.6 关系数据一致性
`Changed` 事件携带完整 `SessionGroup` 快照；订阅方以快照覆盖本地视图，不做增量推断。因 `Changed` 锁外发布可能乱序，订阅方按组内**单调递增 `Version` 序号**（组每次变更 +1，不用 `DateTime`，避免同刻相等）**丢弃陈旧快照**。快照必须在**组锁内克隆**（`SessionGroup` 与 `Members` 为可变引用），锁外发布克隆体，避免订阅方读到正被改写的实例。

---

## 5. 契约层与脊柱层重构：结束本轮

### 5.1 Abstractions

```csharp
public enum ToolTurnDirective { Continue = 0, EndTurn = 1 }

// ToolResult
public ToolTurnDirective TurnDirective { get; set; } = ToolTurnDirective.Continue;
public string? TurnDirectiveReason { get; set; }

// ToolCallEvent
public ToolTurnDirective TurnDirective { get; init; } = ToolTurnDirective.Continue;
public string? TurnDirectiveReason { get; init; }

// LoopCompleteEvent（Events/MessageEventTypes.cs:156）新增
public string? Reason { get; init; }
```

### 5.2 Core `AgentExecutor`
- `ExecuteSingleToolCallAsync`（成功 `:901` / 失败 `:884`）：把指令填入 `ToolCallEvent`。
- `ExecuteToolCallsAsync`（`:476`）：汇总 `endTurn` 返回。
- `RunProducerAsync`：工具步全部结束后若 `endTurn` → `LoopComplete(Success, Reason)` 并 `return`（参照 `:459-467`）。
- **装饰器透传**：成功/普通路径返回内层 `ToolResult`，指令自然保留（`CachedToolDecorator` 返回同一实例）。`RetryToolDecorator` 重试耗尽（`:85-95`）与 `ToolTimeoutDecorator` 超时归一（`:88`）合成的是**失败/超时结果**，按语义**不携带** `EndTurn`。因此 `session_handoff` 必须声明 `[ToolCapability(ToolCapabilityKeys.TimeoutSkip, "true")]`（同 `TaskTool`），避免交接已完成却因超时丢失终止指令。

### 5.3 组变更事件总线（独立于执行流，解决生命周期）
执行事件流的 `CompleteSession` 会清 buffer 且 Router 无重订阅（`ExecutionEventPublisher.cs:132-157`、`SessionEventStreamRouter.cs:341-401`），故用独立总线：

```csharp
// Abstractions/Events/ISessionGroupEventBus.cs
public interface ISessionGroupEventBus
{
    void Publish(SessionGroupChangedEvent evt);
    IAsyncEnumerable<SessionGroupChangedEvent> SubscribeAsync(string groupId, CancellationToken ct);
}

public record SessionGroupChangedEvent
{
    public required string GroupId { get; init; }
    public required string AnchorSessionId { get; init; }
    public string? ActiveSessionId { get; init; }
    public long Version { get; init; }
    public IReadOnlyList<SessionGroupMember> Members { get; init; } = Array.Empty<SessionGroupMember>();
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}
```

- **Core**：`ChannelSessionGroupEventBus`（Singleton，按 groupId 分发；无订阅者丢弃）。
- **桥**：`SessionGroupEventBridge : IHostedService`——`StartAsync` 订阅 `Changed`，把**锁内克隆的组快照**转 `SessionGroupChangedEvent` 发总线；`StopAsync` **退订**（防泄漏）。HostedService 保证启动即激活。
- **订阅方**：WebUI `SessionWindowRegistry`（按 groupId 订阅）。`SessionGroupChangedEvent` 是**独立总线的普通 record（非 `IMessageEvent`）**，不进入执行事件流，故 Gateway 事件映射（`src/gateway/Seeing.Gateway/Mapping/GatewayEventMapper.cs`、`src/gateway/Seeing.Agent.Gateway/Core/GatewayEventMapper.cs`）**无需任何改动**。

---

## 6. 能力模块 `session.tools`

**包：** `src/capabilities/Seeing.Agent.Tools.Session/`，模块 `Id = "session.tools"`（避开 `SessionReloadHandler.ComponentId="session"`）
`ProvidedTools = ["session_list","session_search","session_read","session_trim","session_handoff"]`
`ActivateAsync` 校验 `IExecutionSubmitter`；缺失则不注册 `session_handoff`（其余正常）。

### 6.1 查询总原则
- 检索优先、分段读取；**永不整段返回**
- 三重有界：条数 + 单条字符 + 总字节，超限给续读游标
- **不返回思考过程**：`ReasoningContent` 一律剔除
- 工具调用结果默认仅"工具名 + 状态 + 结果摘要"（`include_tool_output` 可开）
- 输出带稳定 `message_id`；声明 `[ToolCapability(ToolCapabilityKeys.OutputSkip,"true")]`

### 6.2 `session_list`
`scope`(related\|all, 默认 related)、`limit`(30, 上限 100)、`offset`(0)。
related = 组成员；all = `ListAnchorsAsync`/`LoadAllFromStorageAsync` 过滤 Partition。数据源 `SessionData`。
输出：`session_id | relation | kind | title | updated_at | message_count`。

### 6.3 `session_search`（对齐 `grep`）
`session_id`(默认当前)、`pattern`(正则)、`role`、`context`(0, 上限 3)、`limit`(30, 上限 100)、`offset`(0)。
输出：`<message_id>:<role>:step<n>:<preview>`。Metadata：`matches/returned/truncated/next_offset`。

### 6.4 `session_read`（对齐 `read`）
`session_id`(默认当前)、定位三选一（`offset`+`limit` / `from_id`+`to_id` / `message_id`+`before`+`after`）、`limit`(20, 上限 50)、`max_chars`(2000)、`include_tool_output`(false)、`include_compacted`(false)。
输出：`[#<idx> | <message_id> | <role> | step=<n> | <time>]` + 截断内容。

### 6.5 `session_trim`
`mode`(before\|range\|after)、`message_id` 或 `from_id`/`to_id`。

1. 仅活跃消息（已压缩历史不可见、不可裁）。
2. 可裁集 = 活跃消息 − {最后摘要} − {最后 `user` 及其之后}。
3. 目标集 = 参数求交可裁集；空则失败说明。
4. **备份**：`ISessionGroupManager.CreateBackupForkAsync(当前会话, "trim-backup ...")`（**始终入当前组**）；失败 **fail-closed**。
5. 硬删除：`UpdateSessionAsync` → `session.ReplaceMessages(session.Messages.Where(m => !targetIds.Contains(m.Id)))`（**完整 Messages 过滤，保留已压缩历史**）。
6. 配对：结果内嵌 `assistant.ToolCalls[].Result`，删整条即配对；遗留独立 `tool` 角色消息按 `ToolCallId` 整组删。
7. 生效：下一次执行。
8. 返回：`removed_count / backup_session_id / backup_message_count / remaining_active_count`。

### 6.6 `session_handoff`
`prompt`(必填)、`title`(可选)、`agent`(可选)。
能力声明：`[ToolCapability(ToolCapabilityKeys.TimeoutSkip, "true")]` + `[ToolCapability(ToolCapabilityKeys.CacheEnabled, "false")]`（防交接完成却因超时丢失 `EndTurn`）。实现需对"已提交"幂等：重复调用先检查源组 active 是否已指向目标，避免装饰器重试导致重复交接（有回滚兜底）。
1. 解析继承：`PartitionId/WorkingDirectory/SelectedModel/SelectedThinkingEffort/Scenario/SelectedAgent`（`agent` 覆盖）。
2. `CreateHandoffSuccessorAsync(源, agent, title, scenario)` → 建 Clean Root + 入源组 + 置 active（记录原 active 备用）。
3. 设继承项 → `SaveAsync`。
4. `AddMessageAsync(目标, user: prompt)`。
5. `SubmitAsync(目标, ChatInput{prompt}, ChatOptions{SkipUserMessagePersist=true})`（参照 `TaskTool.cs:159-187`）。
6. **失败回滚**：`CancelAsync`（若有 executionId）+ `RemoveSessionAsync(目标)` + 还原原 active → `Failure`。
7. 成功：`Success` + `TurnDirective=EndTurn, Reason="handoff"`，输出 `target session_id`。

### 6.7 权限与并发
- `session_search/session_read`：关联免审；跨组经 `IPermissionAuthorizerFactory`（先例 `AcpTool.cs:92`）审批。
- `session_list(all)` / `session_handoff`：审批。
- `session_trim`：当前会话默认允许（已备份）。
- 组操作幂等 + 存储锁；`Changed` 锁外；handoff 事务式回滚；trim fail-closed。

---

## 7. WebUI：组驱动通用窗口

### 7.1 组件变更
| 组件 | 变更 |
|---|---|
| `Services/ConferenceRegistry.cs` | **删除**，由 `SessionWindowRegistry` 取代 |
| `Services/SessionWindowRegistry.cs`（新） | circuit 维度 `IStreamConsumer`+`IDisposable`；注入 `ISessionGroupManager` + `ISessionGroupEventBus`；`Rebind(sessionId)` **先订阅总线、再读组枚举**（避免竞态丢事件）；按 groupId 订阅；释放时取消订阅 |
| `Pages/Session.razor` | `AllWindows` = 组锚点 + 成员；主窗 = `ResolveActiveId()`；侧栏 = 其余成员；`InitRegistry` → `SessionWindowRegistry` |
| `Pages/Conference.razor` | 同上 |
| `Components/SessionWindow.razor` | 删除 `IsSubAgentView`，改能力标记（§7.2） |
| `Components/SessionToolbar.razor` | 同步能力标记 |
| `Services/SessionWindowContext.cs` | `IsSubAgentView` → 能力标记字段集 |
| `OnTileClick` | → `SetActiveAsync(groupId, sessionId)` |
| 路由初始化 | 打开成员置 active 并持久化；组变更到达时主窗跟随 `ActiveSessionId` |
| `TaskCardAggregator`/`TaskSessionResolver` | 保留（时间线任务卡片，与窗口集合正交） |

### 7.2 能力标记（替换 `IsSubAgentView`）
`IsChild = 成员 Relation == Child`。

| 标记 | Child | 非 Child | 映射现有引用 |
|---|---|---|---|
| `IsChild` | true | false | — |
| `IsReadOnly` | true | false | `SessionWindow:106,990` |
| `AllowAutoApproveChange` | false | true | `SessionWindow:142` |
| `ShowScenarioBadge` | false | true | `SessionToolbar:43` |
| `ShowSubAgentBadge` | true | false | `SessionToolbar:57,70` |
| `ShowAcpModeSelector` | false | true | `SessionToolbar:97` |
| `ShowModelSelector` | false | true | `SessionToolbar:110` |
| `ShowReturnToParent` | 有父时 true | false | `SessionToolbar:135` |
| `CanRename` | false | true | `SessionToolbar:149` |
| `CanDetach` | true | false | `SessionToolbar:151`、`SessionWindow:1243,1249` |
| `CanBranch` | false | true | `Session.razor:104`、`SessionWindow:1243,1249` |
| `CanEditOutboundBinding` | false | true | `SessionToolbar:160` |
| `CanClear` | false | true | `SessionToolbar:174`、`SessionWindow:1298` |
| `CanCreateSession` | false | true | `Session.razor:129` |

写入点：`SessionWindow.razor:228,797`、`SessionWindowContext.cs:28`；`ParentSessionId` 读取点 `SessionWindow:111,705,1264` → `GetParentAsync`。

### 7.3 自动切换（幂等）
后端置 `ActiveSessionId=目标` → `SessionGroupChangedEvent` 经总线 → 主窗切目标、源落 Summary。状态驱动，刷新/重放不重复跳转。

### 7.4 多 Agent 并行
- 每窗口独立 `EventStreamHandler`，组内成员可同时流式执行。
- `ActiveSessionId` 仅展示指针，不参与调度。
- 组持久化成员与 active，刷新恢复多窗布局。

---

## 8. 启用与配置

- `CapabilitySets/BuiltInCapabilitySets.cs`：`full` 必加 `session.tools`；`code` 建议加入。
- `BuiltInScenarios.cs` 无需改；更新 `BuiltInScenariosTests.cs:46-69`。
- 组 store/manager 注册：`Extensions/ServiceCollectionExtensions.cs`（`RegisterCoreServices`，参照 `:438-462`）；同处注册 `SessionForker`（`ILogger<SessionForker>` + `ISessionManager`），并清理 `SessionManager` 工厂中的 `forker: null`。
- 独立包路径：`SessionServiceExtensions.cs`（`AddSessionManager`）同步注册 `ISessionGroupStore`/`ISessionGroupManager`/`SessionForker`，保证不依赖 Core 的独立使用场景也有组能力。
- 重定位：新增 `Configuration/SessionGroupReloadHandler.cs`（模式同 `SessionReloadHandler.cs:30-41`，`ComponentId="session.groups"`）。
- Samples：`WebUI/Program.cs`、`Cli/Infrastructure/CliServiceBootstrap.cs:39-54`、`Embed.Demo/Program.cs` 增 `AddSeeingModule<SessionToolsModule>(registry)`。
- 文档：`docs/architecture/05-extension-guide.md`（能力包可引原语）、`02-modules.md`（登记 `Seeing.Agent.Tools.Session | session.tools`）、`06-compliance-audit.md`（复扫纳入新包）。
- `Seeing.Agent.slnx`：在既有 `/src/capabilities/`、`/tests/capabilities/` Folder（`:33,:73`）内追加项目条目。

---

## 9. 测试策略

| 层 | 测试 |
|---|---|
| 原语 | 组 CRUD、懒建、active 切换/陈旧回退/被删回退、锚点提升、Child/Fork/Backup/Handoff 入组、SubAgent Fork 独立组、**删除级联（含 Child 递归）**、`ListAnchorsAsync` 物化空组、`ListChildrenAsync` 冷存储兜底、序列化往返、`Changed` 锁外触发且带 `Version`、**并发 `EnsureForSessionAsync` 单组不变量**、**非锚点 `Kind==SubAgent ⟺ Relation==Child` 不变量** |
| 脊柱 | `EndTurn` 退出且仅一次 `LoopComplete(Success,Reason)`；装饰器保留指令；`SessionGroupEventBridge` 启动订阅/停止退订；总线按 groupId 分发 |
| 能力 | 5 工具有界/截断/续读；不返回思考；trim 边界（摘要/最后 user/配对/**保留压缩历史**）；备份 fail-closed；权限分支；handoff 继承与回滚；无 `IExecutionSubmitter` 不注册 handoff |
| WebUI | `SessionWindowRegistry` 先订阅后枚举、组枚举/切换/自动切换/并行、释放取消订阅；`ConferenceRegistryTests` 迁移 |

命令：`dotnet build Seeing.Agent.slnx`；单测用 VSTest 运行已构建程序集（AGENTS.md）。

---

## 10. 里程碑与并行工件

| 里程碑 | 内容 | 依赖 | 并行性 |
|---|---|---|---|
| **M1** | 原语：实体/枚举/EventArgs、store（含 `FindBySessionAsync` 与缓存）、manager（全部关系入口 + 级联 + 物化 + ListChildren 冷兜底 + 每会话锁）、`SessionData` 去关系（`Kind` 保留 `SubAgent=2`）、`SessionManager` 降级、`SessionForker` 纯复制化 | — | 起点 |
| **M2** | Abstractions/Core：`ToolTurnDirective`+`LoopComplete.Reason`+装饰器透传+`AgentExecutor` 退出；`ISessionGroupEventBus`+`ChannelSessionGroupEventBus`+`SessionGroupEventBridge`；Core 注册与重定位 | M1 | 可与 M4 并行 |
| **M3** | `session.tools`：5 工具 + 模块 + 单测 | M1,M2 | 可与 M4 并行 |
| **M4** | WebUI：`SessionWindowRegistry`、页面/`SessionWindow`/`SessionToolbar` 组驱动与能力标记 | M1,M2 | 与 M3 并行 |
| **M5** | 启用配置、示例、文档、端到端验证 | M3,M4 | 收敛 |

**关键审查点：** ① 设计定稿（子 Agent 终审签字）；② 规格定稿（用户确认）。

---

## 11. 影响面（完整清单）

**新增**
- `src/primitives/Seeing.Session/Core/SessionGroup.cs`（实体/枚举/EventArgs）
- `src/primitives/Seeing.Session/Core/ISessionGroupManager.cs`
- `src/primitives/Seeing.Session/Storage/ISessionGroupStore.cs`、`FileSessionGroupStore.cs`、`IRelocatableSessionGroupStore.cs`
- `src/primitives/Seeing.Session/Management/SessionGroupManager.cs`
- `src/abstractions/Seeing.Agent.Abstractions/Events/ISessionGroupEventBus.cs`、`SessionGroupChangedEvent`
- `src/spine/Seeing.Agent.Core/Execution/ChannelSessionGroupEventBus.cs`
- `src/spine/Seeing.Agent.Core/Hosting/SessionGroupEventBridge.cs`
- `src/spine/Seeing.Agent.Core/Configuration/SessionGroupReloadHandler.cs`
- `src/capabilities/Seeing.Agent.Tools.Session/`（模块+5 工具+Options+csproj）
- `samples/Seeing.Agent.WebUI/Services/SessionWindowRegistry.cs`
- `tests/primitives/Seeing.Session.Tests/Management/SessionGroupTests.cs`
- `tests/capabilities/Seeing.Agent.Tools.Session.Tests/`
- `tests/apps/Seeing.Agent.WebUI.Tests/Services/SessionWindowRegistryTests.cs`

**修改（原语）**
- `Core/SessionData.cs`、`Core/SessionKind.cs`（删 `Fork`）、`Core/ISessionManager.cs`、`Management/SessionManager.cs`、`Management/SessionForker.cs`
- `Storage/GlobalSessionStore.cs:125-126`
- `SessionServiceExtensions.cs`

**修改（契约/脊柱）**
- `Tools/ToolResult.cs`、`Events/MessageEventTypes.cs`、`Core/AgentExecutor.cs`
- `Core/Extensions/ServiceCollectionExtensions.cs`、`Core/Configuration/SessionReloadHandler.cs`
- `Decorators/RetryToolDecorator.cs:85-95`、`Decorators/ToolTimeoutDecorator.cs:88`
- `CapabilitySets/BuiltInCapabilitySets.cs`、`Services/SessionTitleService.cs:96,166`
- Gateway 事件映射**无需改动**（`SessionGroupChangedEvent` 非 `IMessageEvent`，不进执行流）

**修改（Hosting/Samples）**
- `Hosting/ChatOrchestrator.cs:146-186,138,189-207,222-237`（Create 委托 `CreateRootAsync`、Delete 委托 `RemoveSessionAsync`、Branch 委托 `ForkSessionAsync`）
- `Hosting/Commands/SessionCommands.cs:34-46`（`/fork` 改走组管理器）
- `Hosting/Tools/TaskTool.cs:112,115,137,392`、`Tools/TaskStatusTool.cs:131,214`、`Execution/ExecutionJobService.cs:307,1268,1290`
- `src/capabilities/Seeing.Agent.Scheduler/Jobs/HeartbeatJob.cs:262`（`ListAllAsync` 消费；仅用 Id/LastActiveAt，语义保持）
- `samples/Seeing.Agent.Cli/Commands/SessionCommand.cs:70`、`samples/Seeing.Agent.Cli/Infrastructure/CliServiceBootstrap.cs`
- `samples/Seeing.Agent.WebUI/Program.cs`
- `samples/Seeing.Agent.WebUI/Pages/Session.razor`、`Pages/Conference.razor`、`Pages/SessionsPage.razor:505-517`
- `samples/Seeing.Agent.WebUI/Components/SessionWindow.razor:111,228,705,797,106,142,990,1243,1249,1264,1298,1305-1315`
- `samples/Seeing.Agent.WebUI/Components/SessionToolbar.razor:43,57,70,97,110,135,149,151,160,174`
- `samples/Seeing.Agent.WebUI/Services/SessionWindowContext.cs:28`、`Services/TaskSessionResolver.cs:35,42`
- `samples/Seeing.Agent.Embed.Demo/Program.cs`
- `docs/architecture/05-extension-guide.md`、`02-modules.md`、`06-compliance-audit.md`
- `Seeing.Agent.slnx`（既有 Folder 内追加）

**修改（测试）**
- `tests/primitives/Seeing.Session.Tests/SessionManagerStorageTests.cs:33,73,84,116-117`
- `tests/primitives/Seeing.Session.Tests/Management/SessionChildTests.cs`（整文件，含 Fork/Child）
- `tests/primitives/Seeing.Session.Tests/SessionScenarioTests.cs:92-115`
- `tests/spine/Seeing.Agent.Tests/Sessions/SessionEnhancedTests.cs:21,34,44,50-62,68-115,264`
- `tests/spine/Seeing.Agent.Tests/Scenarios/BuiltInScenariosTests.cs:46-69`
- `tests/spine/Seeing.Agent.Tests/Services/SessionTitleServiceTests.cs`
- `tests/spine/Seeing.Agent.Tests/Tools/TaskToolTests.cs`、TaskStatusTool 相关
- `tests/capabilities/Seeing.Agent.Acp.Tests/AcpToolTests.cs:231,268-330`（fake `ISessionManager` 删除 5 个关系方法：Fork/CreateChild/ListRoots/ListChildren/LoadChildrenFromStorage）
- `tests/apps/Seeing.Agent.WebUI.Tests/Services/TaskSessionResolverTests.cs:15`、`TaskCardAggregatorTests.cs:46,48,187,189,313-317`
- `tests/capabilities/Seeing.Agent.Scheduler.Tests`（`HeartbeatJob`/`ListAllAsync` 路径保持）

**删除**
- `samples/Seeing.Agent.WebUI/Services/ConferenceRegistry.cs`
- `tests/apps/Seeing.Agent.WebUI.Tests/Services/ConferenceRegistryTests.cs`（迁移）

---

## 12. 风险与权衡

| 风险 | 影响 | 缓解 |
|---|---|---|
| `Kind.Fork` 移除 + `SessionManager` 降级 | 编译面广 | §11 全量清单 + M1 全量编译 + 不变量测试；`SubAgent=2` 保留数值防存量反序列化失效 |
| 关系入口收敛后调用方漏改 | 运行期丢会话 | "唯一入口清单"逐项 + `ListAnchorsAsync` 物化兜底 |
| "先会话后组"并发双组 | 会话进错组/孤儿组 | 每会话锁 + 锁内复检 `GroupId` + 缓存索引 |
| 组事件总线订阅生命周期 | 自动切换失效 | 独立总线 + HostedService 启动订阅/停止退订 + registry 先订阅后枚举 + `Version` 丢弃乱序 |
| WebUI 替换 `ConferenceRegistry` | 子代理多窗回归 | §7.2 等价映射 + 迁移测试 |
| 裁剪与在途 Loop 竞态 | 历史错乱 | 硬约束 + 下一执行生效 + 完整 Messages 过滤 |
| handoff 中途失败/超时 | 脏组/孤儿会话/不退出 | 事务式回滚（取消+删除+还原 active）；`timeout.skip` 防超时丢指令 |
| 组存储并发写/读放大 | 丢更新/性能 | 存储锁 + 内存组缓存 + `Changed` 带快照 + 锁外触发 |

---

## 13. 开放问题

1. handoff 仅"完全交接/退出"，不提供"等待目标完成"。
2. 交接链长度不限制。
3. `session_search` 不做跨会话聚合搜索。
4. `session_read include_compacted=true` 展示形态留待 UI 细化。
