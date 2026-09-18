# 10 会话持久化写回 Release Notes

**日期：** 2026-09-18
**分支：** `master`（本次工作区，改动尚未提交）
**设计规格：** [`2026-09-18-session-persistence-write-behind-design.md`](../superpowers/specs/2026-09-18-session-persistence-write-behind-design.md)
**相关模块：** `Seeing.Session`（独立 NuGet 包）

---

## 1. 概要

新增**写回（write-behind）持久化调度**：在任意 `ISessionStore` / `ISessionGroupStore` 之上叠加合并写装饰器，将执行期间的高频全量快照保存按会话去抖合并，并提供显式持久化屏障。

- 调度核心：`CoalescingWriteScheduler<TKey,TValue>`（后端无关）。
- 装饰器：`WriteBehindSessionStore` / `WriteBehindSessionGroupStore`。
- 端口：`IWriteBehindSessionStore` / `IWriteBehindSessionGroupStore` / `IPersistenceFlusher`、`ISessionCatalog`（新增）。
- 配置：`SessionPersistenceOptions`。
- 存储抽象加固：全方法补 `CancellationToken`；`ListAsync` / `LoadAllAsync` / `QueryAsync` 直接返回 `IAsyncEnumerable<T>`；`FileSessionStore` 移除 30s 锁超时。

---

## 2. 背景与动机

执行期间每次 ToolCall 状态变迁、每轮 StreamComplete、schema 快照、循环终态都会触发**全量会话落盘**。`FileSessionStore` 在锁内完成序列化 + 临时文件 + 原子替换（失败退避重试），排队超过 30s 直接抛 `TimeoutException`，在大量子会话并发时频繁失败。`FileSessionGroupStore` 的全局写锁是第二热点。

目标：写入调度与存储后端**正交**（装饰器叠加，JSON/SQLite/远程后端可复用）、高频保存去抖合并、**不再向业务抛锁超时**、提供显式持久化屏障，并加固抽象以支持未来替换 DB。

---

## 3. 新行为：写回调度

### 3.1 调度核心 `CoalescingWriteScheduler<TKey,TValue>`

- **尾沿去抖**：同一键在窗口内后写覆盖前写；默认 `DebounceWindow=400ms`，每次新写入重置静默窗口。
- **饥饿保护**：自该键**首次变脏**起，最迟 `MaxFlushDelay=2s` 必须落盘（即使持续有新写入）；触发时立即 flush。
- **单飞 + 按键失败隔离**：任一时刻仅一个 flush 循环；单键写失败内部按指数退避重试（上限 `MaxRetryBackoff=30s`），**不向 `Enqueue` 传播**，不阻塞其它键。
- **屏障**：
  - `FlushAsync`：等待"调用时刻该键的最新版本（或更新版本）"落盘；受 `ct` 约束，不可恢复失败时抛出（显式屏障语义）。
  - `TryFlushAsync` / `TryFlushAllAsync`：有界等待，失败/超时返回 `false`，**不抛**（读路径活性语义）。
  - `DiscardAsync`：等待在途写完成 → 移除待写 → 置删除墓碑（防删除复活）；后续一次合法 `Enqueue` 会清除墓碑，支持删除后同 id 重建。
- **释放**：`Dispose` / `DisposeAsync` 先做最终 flush，受 `ShutdownFlushTimeout=5s` 约束，超时仅记 Warning（同步释放不使用 `GetAwaiter().GetResult()`）。

### 3.2 装饰器 `WriteBehindSessionStore` / `WriteBehindSessionGroupStore`

- `SaveAsync` = **入队即返回**（不阻塞、不克隆，快照契约见 §4.5）；需要强持久化请改用 `FlushAsync`。
- 读路径（会话：`LoadAsync` / `ListAsync` / `QueryAsync` / `LoadAllAsync`；组：`LoadAsync` / `FindBySessionAsync` / `ListAsync`）先做**有界** flush（`ReadFlushTimeout=1s`）再委托内层，保证"读己所写"；超时或内层持续失败时记 Warning 后继续读旧值，**绝不挂起**。
- `DeleteAsync` 先 `DiscardAsync`（等待在途写并丢弃待写）再删后端，消除删除复活竞态。
- `SaveAllAsync`：逐个入队后 `FlushAllAsync`。
- 实现并转发 `IRelocatableSessionStore` / `IRelocatableSessionGroupStore`（内层不支持时 `SetBaseDirectory` 为 no-op）。
- 同时实现 `IDisposable` + `IAsyncDisposable`（MS DI 同步 `Dispose()` 兼容）。

### 3.3 后端调整

| 后端 | 变更 |
|------|------|
| `FileSessionStore` | 全方法补 ct；`AtomicMoveWithRetryAsync` 的 `Task.Delay` 传 ct；移除 `LockTimeout=30s` 抛错，读/写均 `await fileLock.WaitAsync(ct)` **无超时排队**；移除防御性克隆，仅缺省补 `CreatedAt`（不再改 `UpdatedAt`）；保存成功日志 Info→Debug |
| `FileSessionGroupStore` | 全局 `_writeLock` 改为**按组锁字典**（消除全局串行）；补 ct；保存成功日志 Info→Debug |
| `InMemorySessionStore` | 补 ct；存快照引用（不克隆） |

### 3.4 显式 flush 点

| 位置 | 理由 |
|------|------|
| Host 关闭 / DI dispose | 最终持久化（`IDisposable` + `IAsyncDisposable`） |
| `SessionArchiver` | 归档前确保最新 |
| `SessionForker` | 分支源数据最新 |
| `BuiltInCommands` 保存命令 | 用户显式保存 |
| `GatewaySessionService` / `GatewayOrchestratorV2` | 响应前落盘 |
| `ExecutionJobService` 终态 finally | 执行结束确保落盘 |
| `SessionReloadHandler` | 工作区切换前落盘旧目录 |
| `SessionGroupReloadHandler` | 组写缓冲否则会落到**新**工作区 |

---

## 4. 破坏性变更清单（迁移对照）

### 4.1 契约签名

| # | 类型 / 成员 | 处置 | 迁移 |
|---|-------------|------|------|
| 1 | `ISessionStore` 全部方法 | 补 `CancellationToken ct = default` | 调用点补 ct（可选） |
| 2 | `ISessionStore.ListAsync` / `LoadAllAsync` / `QueryAsync` | 返回类型由 `Task<IAsyncEnumerable<T>>` 改为 **`IAsyncEnumerable<T>`** | 去掉外层 `await`，直接 `await foreach` |
| 3 | `ISessionGroupStore` 全部方法 | 补 `ct`；`ListAsync(ct)` | 调用点补 ct |
| 4 | `SessionManager` 构造参数 | `GlobalSessionStore? globalStore` → **`ISessionCatalog? catalog`** | 命名参数 `globalStore:` 改 `catalog:`，类型改 `ISessionCatalog?` |
| 5 | `ISessionManager` | 新增 `Task FlushAsync(string id, CancellationToken ct = default)` | 自定义实现必须补齐 |
| 6 | `WriteBehindSessionStore.SaveAsync` | 语义 = **已入队**（非已落盘） | 需强持久化改 `FlushAsync` |

### 4.2 快照契约（冻结并行实现）

`SaveAsync` 的入参是**调用方移交的私有快照**：

- 实现方不得修改该实例，也不得依赖调用方后续对活会话的修改；可直接序列化/存储。
- 调用方保证在 `SaveAsync` 返回后不再修改传入实例。
- 时间戳（`UpdatedAt`/`CreatedAt`）由**调用方在克隆前**写入活会话；实现方仅在 `CreatedAt` 缺省时补齐。
- **克隆恰好一次，发生在调用方**：`SessionManager` 先写 `session.UpdatedAt` 再 `session.Clone()`；`SessionGroupManager` 在 `_store.SaveAsync` 前 `group.Clone()`。后端不再克隆。

### 4.3 需同步修改的已知实现 / 调用点

- `FileSessionStore.SaveAllAsync` 内部调用、`ListAsync`/`LoadAllAsync` 返回类型。
- `InMemorySessionStore.SaveAllAsync` 与 `ListAsync`/`LoadAllAsync`。
- `SessionManager.LoadAllFromStorageAsync` 去掉 `await _store.ListAsync()`。
- `SessionGroupManager` 全部 `_store.SaveAsync` 改传 `Clone()` + ct。
- 反射式 `ISessionStoreTests`、`FailingGroupStore : ISessionGroupStore`（测试）等随签名更新。
- `FakeSessionManager : ISessionManager`（`AcpToolTests`）补 `FlushAsync`。

### 4.4 包影响

`Seeing.Session` 为独立 NuGet 包，以上签名破坏属**主版本级**变更，第三方实现需同步。

---

## 5. 兼容性与行为说明

- **单进程独占写**：不做跨进程写入协调；文件锁无超时排队以单进程为前提。
- **文件格式不变**：JSON 全量快照格式与路径约定不变（非增量/journal），外部读取兼容。
- **保存语义**：启用装饰器后 `SaveAsync` 变为入队；"保存即持久化"的可观测语义由读路径的有界 flush 维持。未启用写回（直写后端）时行为与旧版一致。
- **工作区切换**：`SessionReloadHandler` / `SessionGroupReloadHandler` 在重定位前 flush，避免待写内容落到新目录；两处经 `IPersistenceFlusher` 判定，存储未实现该端口时自动 no-op。
- **默认 DI 接线（默认启用写回）**：写回装饰器**默认启用**，并非未接线。`AddSeeingCore` 与 `SessionServiceExtensions.AddSessionManager` 在 `SessionPersistenceOptions.Enabled`（默认 `true`）为真时，将 `ISessionStore` / `ISessionGroupStore` 解析到 `WriteBehindSessionStore` / `WriteBehindSessionGroupStore`，并由其包装 `FileSessionStore` / `FileSessionGroupStore`；§3.4 的显式 flush 点因此实际生效。**旁路写回**：在调用 `AddSeeingCore` / `AddSessionManager` **之前**注册一个 `SessionPersistenceOptions { Enabled = false }` 实例即可（`Enabled=false` 为直写后端，不等价于禁用持久化）。
- **`ISessionCatalog`**：本次仅交付端口与 `GlobalSessionStore` 实现改造，**不注册 DI**（见 §7）。

---

## 6. 配置项 `SessionPersistenceOptions`

命名空间 `Seeing.Session.Persistence`。

| 属性 | 默认 | 说明 |
|------|------|------|
| `Enabled` | `true` | 启用写回（`false` = 直写后端，不等价于禁用持久化） |
| `DebounceWindow` | `400ms` | 尾沿去抖窗口（窗口内后写覆盖前写） |
| `MaxFlushDelay` | `2s` | 自某个键首次变脏起的饥饿保护上限 |
| `MaxRetryBackoff` | `30s` | 单次写入失败后的指数退避上限 |
| `ShutdownFlushTimeout` | `5s` | 释放期最终 flush 等待上限 |
| `ReadFlushTimeout` | `1s` | 读路径 `TryFlushAsync` 有界等待上限 |

---

## 7. 已知边界

| 边界 | 说明 |
|------|------|
| `ISessionCatalog` 未注册 DI | `GetService<ISessionCatalog>()` 为 null，`SessionManager.ListAllAsync` 继续走内存缓存降级，行为不变。原因：`GlobalSessionStore` 按"分区子目录 + `*.session.json`"读取，而 `FileSessionStore` 实际落盘为扁平 `{sessionId}.json`；贸然接线会返回空列表，回归 Admin/Gateway 会话列举。**激活前须先统一路径布局** |
| `SessionArchiver` 未注册 DI | `SessionManager` 以 `archiver: null` 构造，归档 flush 仅在手动构造并注入 `ISessionManager` 时生效 |
| 写回装饰器默认启用 | 默认 `AddSeeingCore` / `AddSessionManager` 已接线写回（`SessionPersistenceOptions.Enabled=true`）；如需直写后端，须在注册前提供 `Enabled=false` 实例（见 §5） |
| 非目标 | 任何 DB 后端、增量/journal 持久化、跨进程写协调 |
| 写回失败可观测性 | 读路径持续失败退化为读旧值并记 Warning；写入重试不向业务传播（显式 `FlushAsync` 除外） |

---

## 8. 迁移指引（第三方 / 自定义实现）

- **自定义 `ISessionStore` / `ISessionGroupStore`**：补 `CancellationToken`；`ListAsync` / `LoadAllAsync`（组 `ListAsync`）改为 `IAsyncEnumerable<T>`；遵守快照契约（不得修改传入实例，可直接序列化）。
- **自定义 `ISessionManager`**：补 `FlushAsync(string id, CancellationToken ct = default)`。
- **`SessionManager` 构造**：`globalStore:` 改名 `catalog:`，类型 `ISessionCatalog?`。
- **强持久化点**：在 `SaveAsync` 后追加 `await sessionManager.FlushAsync(id, ct)`。
- **依赖 `GlobalSessionStore` 具体类型处**：改依赖 `ISessionCatalog`。
- **使用写回**：默认 DI 已启用写回；仅在自建容器/手动装配时需以 `new WriteBehindSessionStore(inner, new SessionPersistenceOptions(), logger)` 包装后端并注册为 `ISessionStore`；组侧同理（旁路见 §5）。
