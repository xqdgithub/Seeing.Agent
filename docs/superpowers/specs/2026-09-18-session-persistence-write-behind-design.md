# Session 持久化写回调度与存储抽象加固设计

- **日期**：2026-09-18
- **状态**：已按首轮独立审查（Issues Found）修订 v2，待复审签字
- **关联**：
  - `src/primitives/Seeing.Session/Storage/ISessionStore.cs`
  - `src/primitives/Seeing.Session/Storage/FileSessionStore.cs`
  - `src/primitives/Seeing.Session/Storage/FileSessionGroupStore.cs`
  - `src/primitives/Seeing.Session/Storage/GlobalSessionStore.cs`
  - `src/primitives/Seeing.Session/Management/SessionManager.cs`
  - `src/primitives/Seeing.Session/Management/SessionGroupManager.cs`
  - `src/primitives/Seeing.Session/SessionServiceExtensions.cs`
  - `src/spine/Seeing.Agent.Core/Extensions/ServiceCollectionExtensions.cs`
  - `src/hosting/Seeing.Agent.Hosting/Execution/ExecutionJobService.cs`
- **背景**：执行期间每次 ToolCall 状态变迁、每轮 StreamComplete、schema 快照、循环终态都会触发**全量会话落盘**。`FileSessionStore` 在锁内完成序列化 + 临时文件 + 原子替换（失败退避重试），排队超过 30s 直接抛 `TimeoutException`，在大量子会话并发时频繁失败，已难以支撑。

---

## 1. 目标 / 非目标

**目标**

1. 写入调度与存储后端**正交**：合并写入作为装饰器叠加在任意 `ISessionStore` 之上，JSON/SQLite/远程后端可复用。
2. 高频保存按会话**去抖合并**（窗口内后写覆盖前写），磁盘写入从 N 次/窗口降为 ~1 次。
3. **不再向业务抛锁超时**：写入失败内部重试，业务路径不被 `TimeoutException` 击穿。
4. 提供显式持久化屏障 `FlushAsync`，用于关闭/归档/Gateway 响应/命令等需强持久化点。
5. 存储抽象加固到可持续换 DB：`CancellationToken`、元数据目录端口化、生命周期/一致性契约明确。
6. 消除第二热点：`FileSessionGroupStore` 全局写锁改为按组。

**非目标**

- 本次**不实现**任何 DB 后端（仅加固抽象使其可替换）。
- 本次**不激活** `ISessionCatalog` 的文件实现接线（见 §3.3，存在布局不一致风险）。
- 不做增量/journal 持久化（保留全量快照格式，兼容外部读取）。
- 不改变会话 JSON 文件格式与路径约定。
- 不做跨进程写入协调（约定单进程独占写）。

---

## 2. 架构总览

```
SessionManager ──> ISessionStore ──> WriteBehindSessionStore ──> FileSessionStore | (未来) SqliteSessionStore
      │                                    │
      │                            CoalescingWriteScheduler
      │
      └──> ISessionCatalog? ───────────> (未来实现；本次不接线)

SessionGroupManager ──> ISessionGroupStore ──> WriteBehindSessionGroupStore ──> FileSessionGroupStore
```

- **端口**：`ISessionStore` / `ISessionGroupStore` / `ISessionCatalog`（纯契约）。
- **调度**：`CoalescingWriteScheduler<TKey,TValue>`（后端无关合并写核心）。
- **装饰器**：`WriteBehindSessionStore` / `WriteBehindSessionGroupStore` 薄适配。
- **后端**：`FileSessionStore` / `FileSessionGroupStore` 保持"每次写入即持久化"语义。

目录约定：新类型置于 `src/primitives/Seeing.Session/Persistence/`。

---

## 3. 抽象加固

### 3.1 `ISessionStore`（破坏性）

```csharp
public interface ISessionStore
{
    Task SaveAsync(SessionData data, CancellationToken ct = default);
    Task<SessionData?> LoadAsync(string sessionId, CancellationToken ct = default);
    Task DeleteAsync(string sessionId, CancellationToken ct = default);
    IAsyncEnumerable<SessionData> ListAsync(CancellationToken ct = default);
    IAsyncEnumerable<SessionData> QueryAsync(string partitionId, string agentId, CancellationToken ct = default);
    Task SaveAllAsync(IEnumerable<SessionData> data, CancellationToken ct = default);
    IAsyncEnumerable<SessionData> LoadAllAsync(CancellationToken ct = default);
}
```

- 去掉 `Task<IAsyncEnumerable<...>>` 双层包装，直接返回 `IAsyncEnumerable`（DB 友好）。
- **快照契约（关键，冻结并行实现）**：`data` 是**调用方移交的私有快照**。实现方：
  - **不得修改或保留对调用方后续可变状态的依赖**；实现可直接序列化/存储该实例。
  - 调用方**保证**在 `SaveAsync` 返回后不再修改传入实例。
  - 时间戳（`UpdatedAt`/`CreatedAt`）由**调用方**在克隆前写入活会话（见 §7.1），实现方仅在缺省时补 `CreatedAt`。
- 因此：**克隆恰好一次，发生在调用方（`SessionManager`）**；后端不再克隆。`InMemorySessionStore` 存该快照引用；`FileSessionStore` 直接序列化该快照。

### 3.2 `ISessionGroupStore`

各方法补 `CancellationToken ct = default`；`ListAsync(ct)`。**采用与 §3.1 相同的快照契约**：`SessionGroupManager` 在调用前克隆组对象（见 §7.1）。

### 3.3 `ISessionCatalog`（新增端口，本次不接线）

```csharp
public interface ISessionCatalog
{
    Task<IReadOnlyList<SessionMetadata>> ListAllAsync(string? partitionId, CancellationToken ct = default);
    Task<SessionStatistics> GetStatisticsAsync(CancellationToken ct = default);
}
```

- `GlobalSessionStore` 实现该接口（保持现有类与目录逻辑不变）。
- `SessionManager` 构造参数由 `GlobalSessionStore?` 改为 `ISessionCatalog?`。
- **本次不注册 `ISessionCatalog` 到 DI**（保持 `GetService<ISessionCatalog>()` 为 null，`ListAllAsync` 继续走内存缓存降级，行为不变）。
- **原因（首轮审查 Blocker）**：`GlobalSessionStore` 按"分区子目录 + `*.session.json`"读取，而 `FileSessionStore` 实际落盘为扁平 `{sessionId}.json`。若贸然接线，`ListAllAsync` 会返回空列表，回归 Admin/Gateway 会话列举。**激活前必须先统一路径布局**，属独立后续任务。
- 本次仅交付端口与实现类改造，使未来替换 DB 目录不需要改动 `SessionManager`。

### 3.4 生命周期

- 调度装饰器**同时实现 `IDisposable` 与 `IAsyncDisposable`**（首轮审查：MS DI 同步 `Dispose()` 遇到仅 `IAsyncDisposable` 的服务会抛异常）：
  - `DisposeAsync`：停止循环并 `FlushAllAsync`，受 `ShutdownFlushTimeout` 约束。
  - `Dispose`：同步 best-effort——触发最终 flush 并最多等待 `ShutdownFlushTimeout`。**避免 `GetAwaiter().GetResult()` 直接占用线程池/上下文**：用带超时的 `Task.Wait(timeout)` 或 `ManualResetEventSlim` 等待完成信号；超时记录 Warning 后返回，不抛。
- `IWriteBehindSessionStore`：显式屏障端口。

```csharp
public interface IWriteBehindSessionStore
{
    Task FlushAsync(string sessionId, CancellationToken ct = default);
    Task<bool> TryFlushAsync(string sessionId, TimeSpan timeout, CancellationToken ct = default);
    Task FlushAllAsync(CancellationToken ct = default);
    Task<bool> TryFlushAllAsync(TimeSpan timeout, CancellationToken ct = default);
}
```

- `FlushAsync`：等待"调用时刻该 key 的最新版本（或更新版本）"落盘；受 `ct` 约束；**不可恢复失败时抛出**（供显式屏障使用）。
- `TryFlushAsync` / `TryFlushAllAsync`：有界等待，**不因内层写失败而抛出**，失败/超时返回 `false`（供读路径使用，保证活性，见 §4）。

### 3.5 能力接口

`IRelocatableSessionStore` / `IRelocatableSessionGroupStore` 保持不变（文件专属能力）。装饰器**实现并转发**：`BaseDirectory => (inner as IRelocatable...)?...`；`SetBaseDirectory` 在 inner 支持时委托，否则 no-op（DB 无需重定位）。保证 `SessionReloadHandler` / `SessionToolOutputStore` 的 `is IRelocatable...` 判定不回归。

---

## 4. 写回调度核心 `CoalescingWriteScheduler<TKey,TValue>`

状态：`ConcurrentDictionary<TKey, TValue> pending`、`ConcurrentDictionary<TKey, DateTimeOffset> firstDirtyAt`、`ConcurrentDictionary<TKey, long> version`、`ConcurrentDictionary<TKey, TaskCompletionSource> flushWaiters`、单信号量、单后台循环。

### 4.1 Enqueue

`void Enqueue(TKey key, TValue snapshot)`：
- `pending[key] = snapshot`（后写覆盖）；`version[key]++`。
- 若该 key 首次 dirty，记录 `firstDirtyAt[key] = now`。
- 发出信号唤醒后台循环。

### 4.2 后台循环（尾沿去抖 + 饥饿保护，精确语义）

```
await signal.WaitAsync(ct)                         // 进入有脏状态
loop:
    var quietDeadline = now + DebounceWindow
    var hardDeadline  = firstDirtyAt(earliest) + MaxFlushDelay
    var waitUntil     = min(quietDeadline, hardDeadline)
    var wait          = waitUntil - now
    if (wait <= 0) break
    if (await signal.WaitAsync(wait, ct)) continue // 有新信号→重算（尾沿重置）
    break                                          // 静默窗口到达
flush pending（见 4.3）
```

- **饥饿保护**：`MaxFlushDelay` 自**该 key 首次 dirty** 起算；每个 Enqueue 仅在该 key 当前非 dirty 时更新 `firstDirtyAt`。触发时**立即 flush**，不再等待 `DebounceWindow`。
- **单飞**：任一时刻仅一个 flush 循环；flush 期间新 dirty 保留至下一轮（`firstDirtyAt` 在 flush 完成时对仍 dirty 的 key 重置）。

### 4.3 flush 一轮

- 取 `pending` 快照（key + value + version），逐个 `await write(value, ct)`。
- 成功：若该 key 的 `version` 未变，则移除 pending/firstDirtyAt 并完成 `flushWaiters[key]`；若已变（flush 期间又 enqueue），保留供下一轮。
- 失败：保留 pending，`firstDirtyAt[key] = now`（重算饥饿窗口），按指数退避（上限 `MaxRetryBackoff`）重试；记 Warning。**不向调用方传播**（`FlushAsync` 的显式等待者除外，见 4.4）。
- 单个 key 失败不阻塞其它 key。

### 4.4 屏障与丢弃

- `Task<bool> TryFlushAsync(key, timeout, ct)`：登记 `flushWaiters[key]`，唤醒循环；等待"版本 ≥ 调用时版本"或超时/ct。写失败持续时超时返回 `false`（不抛）。
- `Task<bool> TryFlushAllAsync(timeout, ct)`：对所有 pending key 同时登记等待者；全部版本达标记为 `true`，任一超时/失败返回 `false`（不抛）。
- `Task FlushAsync(key, ct)`：等价 `TryFlushAsync` 但无超时（受 ct）；若最终因失败无法完成则由内层异常传播（显式屏障语义）。
- `Task DiscardAsync(key, ct)`（首轮审查 Blocker：防删除复活）：**经调度器串行化删除**——等待该 key 在途 flush 完成 → 移除 pending/firstDirtyAt/flushWaiters → 置 tombstone。之后调用方才执行 `inner.DeleteAsync`。
- **tombstone 可复用**（复审建议）：后续一次合法 `Enqueue(key, ...)` 会**清除 tombstone 并正常入队**（支持删除后同 id 重建，避免静默丢数据）。

### 4.5 释放

`Dispose/DisposeAsync`：停止循环 → `FlushAllAsync`（受 `ShutdownFlushTimeout`）→ 释放信号量。

### 4.6 配置 `SessionPersistenceOptions`

| 属性 | 默认 | 说明 |
|---|---|---|
| `Enabled` | `true` | 启用写回（false = 直写后端） |
| `DebounceWindow` | `400ms` | 尾沿去抖窗口 |
| `MaxFlushDelay` | `2s` | 自首次 dirty 起的饥饿保护上限 |
| `MaxRetryBackoff` | `30s` | 单次写入失败退避上限 |
| `ShutdownFlushTimeout` | `5s` | 释放期最终 flush 上限 |
| `ReadFlushTimeout` | `1s` | 读路径 `TryFlushAsync` 上限（§5.1） |

---

## 5. 装饰器

### 5.1 `WriteBehindSessionStore`

实现 `ISessionStore` + `IRelocatableSessionStore` + `IWriteBehindSessionStore` + `IDisposable` + `IAsyncDisposable`。

- `SaveAsync(data, ct)`：`scheduler.Enqueue(data.Id, data)` 立即返回。**不克隆**（`SessionManager` 已移交私有快照，§3.1）。
- `LoadAsync(id, ct)`：`await TryFlushAsync(id, ReadFlushTimeout, ct)`（**有界、不抛**）后 `inner.LoadAsync`。写持续失败时可能读到旧值并记 Warning，**不挂起**。
- `DeleteAsync(id, ct)`：`await scheduler.DiscardAsync(id, ct)`（等待在途写完成并丢弃 pending）后 `inner.DeleteAsync(id, ct)`。消除删除复活竞态。
- `ListAsync` / `QueryAsync` / `LoadAllAsync`：先 `await TryFlushAllAsync(ReadFlushTimeout, ct)` 再委托 inner。**目的**：保持现有"保存即持久化"的可观测语义，避免 `CreateRootAsync → LoadAllFromStorageAsync` 等流程漏读新会话。若超时则继续读并记 Warning（最终一致，显式声明）。
- `SaveAllAsync`：逐个 Enqueue 后 `FlushAllAsync(ct)`。
- 能力转发见 §3.5。

### 5.2 `WriteBehindSessionGroupStore`

实现 `ISessionGroupStore` + `IRelocatableSessionGroupStore` + `IDisposable` + `IAsyncDisposable`。

- `SaveAsync(group, ct)`：`scheduler.Enqueue(group.Id, group)`（`SessionGroupManager` 已移交快照，§3.2）。
- `LoadAsync(groupId, ct)`：`TryFlushAsync(groupId, …)` 后委托。
- `FindBySessionAsync` / `ListAsync`：`TryFlushAllAsync(…)` 后委托（无单一 key，明确为全量 flush）。
- `DeleteAsync(groupId, ct)`：`DiscardAsync` 后委托。

---

## 6. 后端调整

### 6.1 `FileSessionStore`

- 补 `CancellationToken`；`AtomicMoveWithRetryAsync` 的 `Task.Delay` 传入 ct。
- `LockTimeout=30s` 抛错改为 `await fileLock.WaitAsync(ct)` 无超时排队（写回已串行化，争用近零）。**读路径**同样无超时排队（单进程独占、锁内仅 IO；如后续观测到读阻塞再引入有界等待）。
- **移除防御性克隆**（§3.1 契约：input 为调用方私有快照）；不再修改传入对象的 `UpdatedAt`（由调用方设置），仅缺省时补 `CreatedAt`。
- 保存成功日志 Info→Debug；失败保留 Warning/Error。

### 6.2 `FileSessionGroupStore`

- 全局 `_writeLock` 改为**按 group 的锁字典**（消除全局串行）。
- 补 `CancellationToken`；保存成功日志 Info→Debug。

### 6.3 `InMemorySessionStore`

- 补 `CancellationToken`；存快照引用（不克隆，遵循 §3.1）。

---

## 7. `SessionManager` / `SessionGroupManager` 与 DI

### 7.1 管理器

- **`SessionManager.SaveAsync(id)` 保留 `session.Clone()`**（快照唯一克隆点）：先 `session.UpdatedAt = DateTime.Now`，再 `_store.SaveAsync(session.Clone(), ct)`。保存日志 Info→Debug。
- `Delete(string)` 为同步 `bool`，**不得直接 `await` 存储删除**。精确做法：从缓存移除后，经装饰器 `DeleteAsync`（内部先 `DiscardAsync`）以 fire-and-forget 方式删除，并在装饰器 `DeleteAsync` 内部保证"先丢弃 pending 再删、且等待在途写完成"，从而不会复活；删除失败仅记录日志。`SaveAsync` 路径由 `ISessionManager.SaveAsync` 转发，签名中的 `ct` 取 `CancellationToken.None`（`ISessionManager.SaveAsync` 无 ct 参数）。
- 依赖 `ISessionCatalog?` 替代 `GlobalSessionStore?`。
- `ISessionManager` 新增 `Task FlushAsync(string id, CancellationToken ct = default)`：转发 `IWriteBehindSessionStore`，未启用时为 no-op。
- **`SessionGroupManager` 所有 `_store.SaveAsync(group)` 前先 `group.Clone()`**（快照契约，§3.2）。删除/移除成员路径的组保存在 `DiscardAsync`/`DeleteAsync` 顺序上与会话侧一致。

### 7.2 DI 接线

- `AddSessionManager(...)` / `AddSeeingCore`：
  - 内层后端 `FileSessionStore`；
  - 默认用 `WriteBehindSessionStore` 包装为 `ISessionStore`（受 `Enabled` 控制）；
  - 注册 `IWriteBehindSessionStore`、`SessionPersistenceOptions`。
  - **不注册 `ISessionCatalog`**（§3.3）。
- `customStore` 场景可选包装（`Enabled=false` 旁路）。

### 7.3 显式 flush 点（审计清单）

| 位置 | 理由 |
|---|---|
| Host 关闭 / DI dispose | 最终持久化（`IDisposable` + `IAsyncDisposable`） |
| `SessionArchiver` | 归档前确保最新 |
| `SessionForker` | 分支源数据最新 |
| `BuiltInCommands` 保存命令 | 用户显式保存 |
| `GatewaySessionService` / `GatewayOrchestratorV2` | 响应前落盘 |
| `ExecutionJobService` 终态 finally | 执行结束确保落盘 |
| `SessionReloadHandler` 工作区切换前 | 切换目录前落盘旧目录 |
| **`SessionGroupReloadHandler` 工作区切换前** | 首轮审查：组写缓冲否则会落到**新**工作区；与会话侧对称加 flush |

---

## 8. 破坏性变更清单（含首轮审查补充的具体点位）

1. `ISessionStore`：全方法加 `CancellationToken`；`ListAsync`/`LoadAllAsync` 返回 `IAsyncEnumerable<T>`。
2. `ISessionGroupStore`：全方法加 `CancellationToken`；补快照契约。
3. `SessionManager` 构造参数 `GlobalSessionStore?` → `ISessionCatalog?`。
4. `ISessionManager` 新增 `FlushAsync`。受影响实现：
   - `tests/capabilities/Seeing.Agent.Acp.Tests/AcpToolTests.cs:231,260,277` 的 `FakeSessionManager : ISessionManager` 需补实现。
5. `WriteBehindSessionStore.SaveAsync` 语义 = 已入队；需强持久化改 `FlushAsync`。
6. 需同步修改的已知实现/调用点：
   - `FileSessionStore.SaveAllAsync` 内部调用（`FileSessionStore.cs:518`）适配新签名；`ListAsync`/`LoadAllAsync` 返回类型。
   - `InMemorySessionStore.SaveAllAsync`（`:132`）与 `ListAsync`/`LoadAllAsync`。
   - `SessionManager.LoadAllFromStorageAsync`（`SessionManager.cs:727`）去掉 `await _store.ListAsync()`。
   - `tests/primitives/Seeing.Session.Tests/Management/SessionHandoffRollbackTests.cs:97` 的 `FailingGroupStore : ISessionGroupStore` 补 ct。
   - 反射式 `ISessionStoreTests.cs` 随签名更新。
   - `SessionGroupManager` 全部 `_store.SaveAsync` 调用改传克隆 + ct。
7. `Seeing.Session` 为独立 NuGet 包，以上需在 Release Notes 标注并同步架构文档。

---

## 9. 测试策略

- `CoalescingWriteSchedulerTests`：窗口合并（N Enqueue → 1 write）、last-writer-wins、饥饿保护（连续 Enqueue 仍不超过 `MaxFlushDelay` 落盘）、失败重试不向 Enqueue 传播、`FlushAsync`/`TryFlushAsync` 版本语义与有界语义、`DiscardAsync` 等待在途写、释放 flush。用短窗口/`TimeProvider`。
- `WriteBehindSessionStoreTests`：Save→Enqueue、Load 有界 flush 读己所写、Load 在内层持续失败时**不挂起**、Delete 经 `DiscardAsync` 不复活、List/Query 先 flush、能力转发、`SaveAllAsync` flush。
- `WriteBehindSessionGroupStoreTests`：同上（组维度、`FindBySessionAsync`/`ListAsync` 全量 flush）。
- `FileSessionStoreTests`：无超时排队、ct 生效、**快照契约**（保存后修改原对象不影响已存内容）。
- 现有反射式 `ISessionStoreTests` 随签名更新。
- 全量 `Seeing.Session.Tests` + 主库 session 相关测试回归。

---

## 10. 实施阶段与并行工作流

依据首轮审查的并行边界（按文件所有权划分，避免写冲突）：

| 流 | 内容 | 文件所有权 | 依赖 |
|---|---|---|---|
| **A** | 契约根（串行根，最先冻结）：接口签名、`ISessionCatalog`、`GlobalSessionStore`、`ISessionManager`、`SessionManager` | `Storage/ISessionStore.cs`、`Storage/ISessionGroupStore.cs`、`Storage/ISessionCatalog.cs`、`Storage/GlobalSessionStore.cs`、`Core/ISessionManager.cs`、`Management/SessionManager.cs` + 对应测试改编 | 无（起点） |
| **B** | 调度核心：`SessionPersistenceOptions`、`CoalescingWriteScheduler`、`IWriteBehindSessionStore` + 单测 | `Persistence/*`（新建） | 仅依赖 §4/§5 冻结语义，可与 A 并行 |
| **C** | 装饰器 + 单测 | `Persistence/WriteBehind*Store.cs` | A 契约冻结 + B |
| **D** | 后端：`FileSessionStore`/`FileSessionGroupStore`/`InMemorySessionStore` + 单测 | `Storage/*`（A 之外的后端文件） | A 签名冻结 |
| **E** | 接线与 flush 点：DI、`SessionReloadHandler`、`SessionGroupReloadHandler`、`SessionGroupManager` 克隆、Gateway/命令/执行终态 | `SessionServiceExtensions.cs`、`ServiceCollectionExtensions.cs`、`Configuration/*`、`Management/SessionGroupManager.cs`、hosting/gateway 调用点 | C + D |
| **F** | 回归 + 文档（Release Notes、架构文档） | `docs/*`、回归测试 | E 之后串行收尾 |

串行链：**A → D → E**；**A + B → C → E**；**E → F**。A 为唯一强串行瓶颈，先冻结签名再放并行。

---

## 11. 开放问题

- 无。关键决策已定：写回装饰器正交叠加、单进程独占写、全量快照（非增量）、克隆唯一发生在管理器、本次不含 DB 实现、`ISessionCatalog` 本次不接线。
