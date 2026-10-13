# 15 会话归档与延迟加载 Release Notes

**日期：** 2026-10-13
**设计规格：** [`2026-10-13-session-archival-lazy-loading-design.md`](../superpowers/specs/2026-10-13-session-archival-lazy-loading-design.md)
**相关模块：** `Seeing.Session`（独立 NuGet 包）、`Seeing.Agent.Hosting`、`Seeing.Agent.Abstractions`、`samples/Seeing.Agent.WebUI`、`samples/Seeing.Agent.Tui`

---

## 1. 概要

解决"启动全量加载会话导致内存大、启动慢"的问题：

- **延迟加载**：会话列表只加载**元数据摘要**（`SessionSummary`，不含消息体）；进入会话时才加载完整 `SessionData`。
- **自动归档**：启动时扫描一次，**按组整体归档**不活跃会话（含子 / 分支 / 交接）。
- **存储端口扩展**：归档/摘要能力落在**可替换的存储端口**上，任意后端可独立实现。

---

## 2. 新增存储端口（`Seeing.Session.Storage`）

| 端口 | 职责 | 未实现时的降级 |
|------|------|----------------|
| `ISessionSummaryProvider` | 轻量摘要枚举（不含消息体） | 扩展方法 `ListSummariesAsync` 流式投影 `ISessionStore.ListAsync`（逐条投影，不整体物化） |
| `ISessionArchiveStore` | 单会话归档原语（`ArchiveAsync`/`RestoreAsync`/`ListArchivedAsync`） | `ArchiveAsync`/`RestoreAsync` 抛 `NotSupportedException`；`ListArchivedAsync` 返回空 |

- 新增轻量 DTO `Seeing.Session.Core.SessionSummary`（只读字段，`FromSession` 投影）。
- `SessionStoreExtensions` 提供降级扩展方法（`ListSummariesAsync` / `SupportsArchive` / `ArchiveAsync` / `RestoreAsync` / `ListArchivedAsync`）。

## 3. 后端实现

| 后端 | 摘要 | 归档 |
|------|------|------|
| `FileSessionStore` | `Utf8JsonReader` 流式只读顶层字段，`Skip` 掉 `messages` 数组体（不构建 DOM、不构造消息对象），仅统计元素个数 | 读全量 → gzip 写 `_archive/{id}.json.gz`（temp+原子替换）→ 删活跃文件（含 `.ref`）；`RestoreAsync` 反向 |
| `InMemorySessionStore` | 内存投影 | 移入内存归档字典 |
| `WriteBehindSessionStore` | 转发（读前有界 flush） | 转发：先 `FlushAsync`（落最新快照）再 `DiscardAsync`（设墓碑），消除"归档到旧内容"与"归档复活"两种竞态；能力检查前置 |

> **文件格式不变**：活跃 `{id}.json` 布局不变；归档为 `_archive/{id}.json.gz`（与既有 `SessionArchiver` 一致）。
> **能力探测**：`SessionStoreExtensions.SupportsArchive` 对写回装饰器递归按其内层能力判定（`IWriteBehindSessionStore.InnerSupportsArchive`），避免"装饰器恒支持"导致的误判。
> **状态保留**：归档时原始 `Status` 存入 `Metadata["archive.pre_status"]`，恢复时还原并刷新 `LastActiveAt`，避免丢失 `Completed/Idle/Error` 语义。

## 4. 上层 API

### `ISessionManager` 新增

- `ListSummariesAsync` / `ListArchivedAsync`（轻量，不填充内存缓存）
- `RestoreAsync`
- `EvictAsync` / `EvictAll`（释放内存缓存，不删盘）
- `ArchiveAsync` 改走 `ISessionArchiveStore` 端口（能力缺失抛 `NotSupportedException`）

### `ISessionGroupManager` 新增

- `ListAnchorSummariesAsync`（**不物化组、不写盘**，替代列表路径的 `ListAnchorsAsync`）
- `ArchiveGroupAsync` / `RestoreGroupAsync`（按组整体归档/恢复）
- `ListGroupsAsync`

### `ISessionDirectory` 新增

- `ListSummariesAsync` / `ListArchivedAsync` / `ArchiveAsync` / `RestoreAsync` / `EvictAsync`

## 5. 启动扫描归档

- `SessionArchivalScanService`（`BackgroundService`，随 `AddExecutionEngine` 注册）。
- 配置 `SessionArchivalOptions`：`Enabled`（默认 true）、`InactivityThreshold`（默认 30 天）、`MinMessageCount`（默认 0）。
- 规则：按组整体归档（组内任一成员活跃即跳过）；执行中的组保守跳过；失败隔离；幂等。
- **不阻塞 Host 启动主流程**。

## 6. WebUI / TUI 接入

- `SessionsPage.razor`：列表改走 `ListSummariesAsync`；新增"归档"按钮、"已归档"视图与"恢复"按钮。
- `Index.razor` / `Session.razor` / `SessionWindow.razor`：取数改用摘要。
- TUI：`/sessions`、`/resume`、`ContinueLatestAsync` 改用摘要。

## 7. 破坏性变更

| # | 变更 | 迁移 |
|---|------|------|
| 1 | `ISessionManager` 新增 5 个成员 | 自定义实现须补齐；测试 `FakeSessionManager` 已同步 |
| 2 | `ISessionGroupManager` 新增 3 个成员 | 自定义实现须补齐 |
| 3 | `ISessionDirectory` 新增 5 个成员 | 自定义实现须补齐 |
| 4 | `ISessionManager.ArchiveAsync` 语义改为走端口 | 能力缺失抛 `NotSupportedException`；源不存在返回 `false` |
| 5 | `ISessionArchiveStore.ArchiveAsync` 返回 `bool` | 端口级成功语义（源不存在返回 `false`） |
| 6 | `ISessionStore` 新增**可选端口**（不改既有方法） | 旧后端零改动（降级） |

## 8. 已知边界

| 边界 | 说明 |
|------|------|
| 仅启动扫描 | 运行期间新产生的不活跃会话不会自动归档，直到下次启动 |
| 无独立索引 | 每次列表仍扫描目录（流式只读顶层字段，快于全量反序列化） |
| 无 LRU 驱逐 | 已加载会话常驻，直到显式 `EvictAsync` 或归档 |
| 执行中判定 | 依赖 `ISessionRuntime.GetOverview`；不可判定时保守跳过 |
| `SessionArchiver` | 归档能力已上收至 `ISessionArchiveStore`；旧类保留但不再接线 |
| 配置来源 | `SessionArchivalOptions` 以单例注册，暂未接入 `seeing.json` 配置节绑定 |