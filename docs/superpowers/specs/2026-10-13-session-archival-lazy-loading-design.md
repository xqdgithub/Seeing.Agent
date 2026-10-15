# 会话归档与延迟加载设计

**日期：** 2026-10-13
**状态：** 已实施
**目标框架：** .NET 10.0
**相关模块：** `Seeing.Session`（独立 NuGet 包）、`Seeing.Agent.Hosting`、`Seeing.Agent.Core`、`samples/Seeing.Agent.WebUI`

---

## 1. 背景与问题

当前启动与会话列表加载存在两个性能问题：

1. **启动全量加载**：`SessionManager.LoadAllFromStorageAsync` 通过 `ISessionStore.ListAsync` 枚举并**完整反序列化每个会话（含全部 messages）**，然后全部塞进常驻内存 `_sessionDataCache`（`SessionManager.cs:775`）。
2. **列表即全量**：`SessionGroupManager.ListAnchorsAsync` 为了列出锚点会话，先调用 `LoadAllFromStorageAsync` 全量加载，再给每个未入组会话**物化建组并写盘**（`SessionGroupManager.cs:464`）。WebUI / TUI 的会话列表（`ISessionDirectory.ListAsync`）都走这条链路。

后果：会话数量增长后，**启动慢、内存占用大**；而列表页实际只需要标题、时间、消息数等元数据。

### 现状约束

- **存储是可替换端口**（`ISessionStore` / `ISessionGroupStore`）。当前端口只有返回**完整** `SessionData` 的 `ListAsync`，缺"摘要"和"归档"能力。
- `GlobalSessionStore`（`ISessionCatalog`）虽有元数据读取雏形，但按 `分区/*.session.json` 布局读取，与实际扁平 `{id}.json` 不匹配，**至今未注册 DI**（见 `10-session-persistence-release-notes.md` §7）。这正说明"旁路独立元数据源"易与主存储不一致。
- `SessionArchiver` 已存在（gzip 到 `_archive`），但**未注册 DI、无自动触发**。

因此本设计**不按 JSON 文件硬编码**，而是在**存储端口上扩展能力**，保证任意后端（文件 / 内存 / 未来 DB / 远程）可替换。

---

## 2. 目标与非目标

### 目标

- 会话列表默认只加载**元数据摘要**，不加载 messages。
- 进入具体会话时才加载完整 `SessionData`（延迟加载）。
- 启动时扫描并**自动归档不活跃会话**（按组整体归档）。
- 归档能力落在可替换的存储端口上，各后端可独立实现。
- 提供内存释放入口（`EvictAsync`）。
- WebUI 提供"已归档会话"查看与恢复入口。

### 非目标

- 不做后台定时归档（本设计仅启动时扫描归档）。
- 不做独立元数据索引文件（避免一致性复杂度）。
- 不做内存 LRU 主动驱逐（仅提供显式释放入口）。
- 不引入 DB 后端；仅保证端口设计可被 DB 复用。
- 不改动 `SessionData` 的消息懒加载语义（列表改用独立 `SessionSummary`）。

---

## 3. 架构：存储端口能力扩展

在 `Seeing.Session` 原语层新增两个**可选能力端口**（端口隔离：后端按需实现，未实现则降级）。

### 3.1 新增端口

```csharp
namespace Seeing.Session.Storage;

/// <summary>会话摘要端口：提供不含 messages 的轻量列表，用于会话列表/归档列表。</summary>
public interface ISessionSummaryProvider
{
    /// <summary>枚举全部活跃会话摘要（不含 messages）。</summary>
    IAsyncEnumerable<SessionSummary> ListSummariesAsync(CancellationToken ct = default);
}

/// <summary>会话归档端口：单会话归档原语；按组整体归档的编排在上层。</summary>
public interface ISessionArchiveStore
{
    /// <summary>归档单个会话：从活跃集移出、保留可恢复数据。</summary>
    Task ArchiveAsync(string sessionId, CancellationToken ct = default);

    /// <summary>从归档恢复单个会话回活跃集；返回是否成功。</summary>
    Task<bool> RestoreAsync(string sessionId, CancellationToken ct = default);

    /// <summary>枚举已归档会话摘要。</summary>
    IAsyncEnumerable<SessionSummary> ListArchivedAsync(CancellationToken ct = default);
}
```

### 3.2 摘要 DTO `SessionSummary`

新增轻量只读 DTO（不引用 `SessionData.Messages`）：

```csharp
namespace Seeing.Session.Core;

/// <summary>会话摘要：会话列表/归档列表展示所需的元数据（不含消息体）。</summary>
public sealed class SessionSummary
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? PartitionId { get; init; }
    public string? SelectedAgent { get; init; }
    public string? SelectedModel { get; init; }
    public SessionStatus Status { get; init; }
    public SessionKind Kind { get; init; }
    public string? GroupId { get; init; }
    public bool IsArchived { get; init; }
    public int MessageCount { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public DateTime LastActiveAt { get; init; }
    public string? ChannelId { get; init; }
    public string? UserId { get; init; }
    public string? WorkingDirectory { get; init; }
    public string? Scenario { get; init; }
    public SessionAutoApprove AutoApprove { get; init; }

    /// <summary>从完整会话投影摘要（降级路径使用）。</summary>
    public static SessionSummary FromSession(SessionData s) => new()
    {
        Id = s.Id,
        Title = s.Title,
        PartitionId = s.PartitionId,
        SelectedAgent = s.SelectedAgent,
        SelectedModel = s.SelectedModel,
        Status = s.Status,
        Kind = s.Kind,
        GroupId = s.GroupId,
        IsArchived = s.IsArchived,
        MessageCount = s.MessageCount,
        CreatedAt = s.CreatedAt,
        UpdatedAt = s.UpdatedAt,
        LastActiveAt = s.LastActiveAt,
        ChannelId = s.ChannelId,
        UserId = s.UserId,
        WorkingDirectory = s.WorkingDirectory,
        Scenario = s.Scenario,
        AutoApprove = s.AutoApprove
    };
}
```

### 3.3 降级扩展方法（可替换性关键）

```csharp
namespace Seeing.Session.Storage;

public static class SessionStoreExtensions
{
    /// <summary>
    /// 列出会话摘要：后端实现 <see cref="ISessionSummaryProvider"/> 时原生调用；
    /// 否则回退 <see cref="ISessionStore.ListAsync"/> 并流式投影（不驻留内存）。
    /// </summary>
    public static IAsyncEnumerable<SessionSummary> ListSummariesAsync(
        this ISessionStore store, CancellationToken ct = default);

    /// <summary>后端是否支持归档能力。</summary>
    public static bool SupportsArchive(this ISessionStore store)
        => store is ISessionArchiveStore;
}
```

- 旧 / 第三方后端**零改动**即兼容：无摘要能力时降级为流式投影 `ListAsync`（逐条投影、逐条 yield，不整体物化）。
- 无归档能力时上层调用抛 `NotSupportedException`（能力显式缺失，不静默降级为"删除"）。

### 3.4 各后端实现

| 后端 | 摘要（`ISessionSummaryProvider`） | 归档（`ISessionArchiveStore`） |
|------|-----------------------------------|-------------------------------|
| `FileSessionStore` | 流式 `Utf8JsonReader` 只读顶层字段（跳过 `messages` 数组，不构造 `SessionMessage`） | 读全量 → gzip 写 `_archive/{id}.json.gz` → 删除活跃 `{id}.json`；`RestoreAsync` 反向；`ListArchivedAsync` 扫描 `_archive` |
| `InMemorySessionStore` | 内存投影 `SessionSummary.FromSession` | 移入内存归档字典 `_archived`；`RestoreAsync` 移回；`ListArchivedAsync` 投影 |
| 未来 DB | 查列（不含 messages 列） | `archived` 标志列或归档表 |

> `FileSessionStore` 的摘要读取复用现有文件锁与损坏文件跳过策略；不新增文件格式。

### 3.5 写回装饰器转发

`WriteBehindSessionStore` / `WriteBehindSessionGroupStore` **转发**新端口：

- 若内层实现 `ISessionSummaryProvider`：装饰器也实现该端口，转发前做有界 `TryFlushAllForReadAsync`（保证"读己所写"，复用现有 `ReadFlushTimeout`）。
- 若内层实现 `ISessionArchiveStore`：装饰器也实现，`ArchiveAsync` 前先 `FlushAsync(sessionId)` 确保最新快照落盘，再委托内层归档。
- 内层未实现对应端口时，装饰器**不实现**该端口，让上层扩展方法走降级路径。

---

## 4. 上层管理器改动

### 4.1 `ISessionManager` 新增

```csharp
/// <summary>列出会话摘要（轻量，不含 messages）。</summary>
IAsyncEnumerable<SessionSummary> ListSummariesAsync(CancellationToken ct = default);

/// <summary>列出已归档会话摘要。</summary>
IAsyncEnumerable<SessionSummary> ListArchivedAsync(CancellationToken ct = default);

/// <summary>归档单个会话（能力缺失时抛 NotSupportedException）。</summary>
Task ArchiveAsync(string sessionId, CancellationToken ct = default);   // 已有，改为走端口

/// <summary>从归档恢复单个会话到活跃集。</summary>
Task<bool> RestoreAsync(string sessionId, CancellationToken ct = default);

/// <summary>从内存缓存释放指定会话（不删除磁盘数据）。</summary>
Task EvictAsync(string sessionId, CancellationToken ct = default);

/// <summary>释放全部内存缓存（不删除磁盘数据）。</summary>
void EvictAll();
```

- `LoadAllFromStorageAsync` **保留**，不再被启动/列表路径调用（列表改走 `ListSummariesAsync`）；仍供 `SessionListTool` 等需要完整数据的调用方使用。
- 现有 `ArchiveAsync(string)` 实现改为委托 `ISessionArchiveStore`；`SessionArchiver` 迁移为 `FileSessionStore` 的归档能力实现（或独立 `FileSessionArchiveStore` 适配器），并在 DI 中注册。归档前保留现有 `FlushAsync` 屏障逻辑。
- `EvictAsync`：从 `_sessionDataCache` 移除；若会话正在执行（有在途消费循环）则**跳过并记 Warning**（由调用方保证不在执行中释放）。

### 4.2 `ISessionGroupManager` 新增

```csharp
/// <summary>列出锚点会话摘要（轻量；不再全量加载 messages）。</summary>
Task<IReadOnlyList<SessionSummary>> ListAnchorSummariesAsync(string? partitionId = null, CancellationToken ct = default);

/// <summary>按组整体归档：归档组内全部会话（含子/分支/交接）。</summary>
Task<bool> ArchiveGroupAsync(string groupId, CancellationToken ct = default);

/// <summary>恢复归档组：恢复组内全部会话并重建组。</summary>
Task<bool> RestoreGroupAsync(string groupId, CancellationToken ct = default);
```

- `ListAnchorSummariesAsync`：基于 `ListSummariesAsync` 摘要，避免加载 messages；**不再为未入组会话写盘建组**（改为在真正进入会话时才 `EnsureForSessionAsync`）。
  - 锚点判定：经 `ISessionGroupStore.ListAsync` 枚举全部组（组对象不含消息体、数量远小于会话数），收集 `AnchorSessionId` 集合，再据 `SessionSummary.GroupId` 过滤出锚点摘要。
  - **未入组会话的处理**：无 `GroupId` 的会话摘要即视为其自身的锚点（单成员组语义），列表直接展示，**不物化组、不写盘**；进入会话时由 `EnsureForSessionAsync` 物化。
  - 归档会话（`IsArchived`）从活跃列表排除。

### 4.3 `ISessionDirectory` 新增

```csharp
/// <summary>列出会话摘要（轻量列表，替代全量 ListAsync 用于列表页）。</summary>
Task<IReadOnlyList<SessionSummary>> ListSummariesAsync(CancellationToken ct = default);

/// <summary>列出已归档会话摘要。</summary>
Task<IReadOnlyList<SessionSummary>> ListArchivedAsync(CancellationToken ct = default);

/// <summary>归档会话（按组整体归档）。</summary>
Task ArchiveAsync(string sessionId, CancellationToken ct = default);

/// <summary>恢复归档会话（按组整体恢复）。</summary>
Task RestoreAsync(string sessionId, CancellationToken ct = default);

/// <summary>从内存释放会话。</summary>
Task EvictAsync(string sessionId, CancellationToken ct = default);
```

- `ListAsync()`（返回完整 `SessionData`）**保留**，但 WebUI 列表页改用 `ListSummariesAsync`。
- `GetAsync` 保持"缓存优先 → 磁盘加载"，即进入会话时的延迟加载入口。

### 4.4 `ISessionGroupStore` 摘要能力

**决策：复用现有 `ISessionGroupStore.ListAsync`，不引入组摘要端口。**

理由：`SessionGroup` 仅含关系元数据（成员 ID、关系、顺序），**不含消息体**，单个对象体量小；组数量远小于会话数。列表路径枚举组仅用于收集 `AnchorSessionId` 集合，开销可接受。

> 仅当实测组数量/体量成为瓶颈时，才考虑引入 `ISessionGroupSummaryProvider`（可选端口，模式同 §3.1）。本次不实现。

---

## 5. 启动扫描归档

### 5.1 触发

仅**启动时扫描一次**，不阻塞 Host 启动主流程（`IHostedService.StartAsync` 内 fire-and-forget 或 `BackgroundService`）。

### 5.2 流程

```
启动扫描（BackgroundService）
  → 读取 SessionArchivalOptions
  → 若 !Enabled：退出
  → 通过 ISessionGroupStore.ListAsync 枚举全部组 + ISessionStore.ListSummariesAsync 枚举全部会话摘要
  → 对每个"组"计算组内最新 LastActiveAt
      → 若组内最新 LastActiveAt < now - InactivityThreshold
      → 且组内无会话处于执行中（通过 ISessionRuntime 查询；不可判定时保守跳过）
      → ArchiveGroupAsync(groupId)
  → 对无组的独立会话摘要：LastActiveAt < 阈值则 ArchiveAsync(sessionId)
  → 记录统计日志
```

### 5.3 关键规则

- **按组整体归档**：组内任一成员活跃即不归档；避免孤立子会话。
- **执行中保护**：有在途执行（消费循环未终态）的会话 / 组不归档。
- **幂等**：已归档（`IsArchived`）的会话跳过。
- **失败隔离**：单组归档失败仅记 Warning，继续其余组。

---

## 6. 配置项

新增 `SessionArchivalOptions`（命名空间 `Seeing.Session.Persistence` 或 `Seeing.Session.Storage`）：

| 属性 | 默认 | 说明 |
|------|------|------|
| `Enabled` | `true` | 是否启用启动扫描归档 |
| `InactivityThreshold` | `30 天` | 不活跃阈值（`LastActiveAt` 早于 `now - 阈值` 视为不活跃） |
| `ScanOnStartup` | `true` | 启动时扫描（预留扩展点，本次恒为 true） |
| `MinMessageCount` | `0` | 消息数下限：低于此值不归档（避免归档空会话；0 = 不限制） |

配置来源：复用 `.seeing/seeing.json` 配置节约定（经 `AddSeeingCore` 的 `IConfigSectionRegistry`），或作为 `SessionArchivalOptions` 单例注册供宿主覆盖（与 `SessionPersistenceOptions` 一致的旁路模式）。

---

## 7. 内存释放

- `EvictAsync(sessionId)` / `EvictAll()`：从 `SessionManager._sessionDataCache` 移除。
- **归档自动移除**：`ArchiveGroupAsync` / `ArchiveAsync` 成功后，自动 `EvictAsync` 相关会话。
- **不主动 LRU 驱逐**：由 UI 或宿主显式调用释放入口。
- 释放不影响磁盘；下次访问经 `GetAsync` 重新加载。

---

## 8. WebUI 改动

| 页面 / 组件 | 改动 |
|-------------|------|
| `SessionsPage.razor` | `SessionDirectory.ListAsync()` → `ListSummariesAsync()`；列表项字段改用 `SessionSummary`；新增"归档"操作按钮 |
| `Index.razor` / `Session.razor` | 列表取数改用 `ListSummariesAsync()`；进入会话仍走 `GetAsync`（延迟加载） |
| 新增"已归档"视图 | 列出 `ListArchivedAsync()`，提供"恢复"按钮 → `RestoreAsync` |
| TUI `TuiSessionController` | `ListAsync` 改用摘要（`ContinueLatestAsync` 只需 `UpdatedAt`/`Id`） |

> 列表页的 `session.MessageCount`、`Title`、`UpdatedAt`、`WorkingDirectory` 等均已在 `SessionSummary` 中提供，无需完整会话。

---

## 9. 兼容性与破坏性变更

`Seeing.Session` 为独立 NuGet 包，端口新增属**次要/主版本级**变更：

| # | 类型 / 成员 | 处置 | 迁移 |
|---|-------------|------|------|
| 1 | `ISessionStore` 新增可选端口 `ISessionSummaryProvider` / `ISessionArchiveStore` | 端口隔离，**不修改** `ISessionStore` 既有方法 | 旧实现零改动（降级） |
| 2 | `ISessionManager` 新增 `ListSummariesAsync` / `ListArchivedAsync` / `RestoreAsync` / `EvictAsync` / `EvictAll` | 新增接口方法 | 自定义实现须补齐；`FakeSessionManager`（测试）同步 |
| 3 | `ISessionManager.ArchiveAsync` 语义 | 改走 `ISessionArchiveStore` 端口 | 能力缺失时抛 `NotSupportedException` |
| 4 | `ISessionGroupManager` 新增 `ListAnchorSummariesAsync` / `ArchiveGroupAsync` / `RestoreGroupAsync` | 新增接口方法 | 自定义实现须补齐 |
| 5 | `ISessionDirectory` 新增 `ListSummariesAsync` / `ListArchivedAsync` / `ArchiveAsync` / `RestoreAsync` / `EvictAsync` | 新增接口方法 | 自定义实现须补齐 |
| 6 | `LoadAllFromStorageAsync` | 保留，不再被启动/列表路径调用 | 列表迁移到 `ListSummariesAsync` |
| 7 | 新增 `SessionSummary` DTO | 新增类型 | 无 |

- **文件格式不变**：归档文件为 `_archive/{id}.json.gz`（与现有 `SessionArchiver` 一致）；活跃 `{id}.json` 布局不变。
- **写回装饰器**：新端口转发；未启用写回时行为不变。

---

## 10. 测试策略

| 层 | 用例 |
|----|------|
| `FileSessionStore` | 摘要读取不含 messages（大 messages 文件仍快速）；归档后活跃目录无该文件、`_archive` 有 `.json.gz`；`RestoreAsync` 还原；`ListArchivedAsync` 列出 |
| `InMemorySessionStore` | 摘要投影正确；归档/恢复；归档列表 |
| `SessionStoreExtensions` | 未实现摘要端口的后端走降级投影且结果一致；无归档能力时 `SupportsArchive=false` |
| `WriteBehindSessionStore` | 转发摘要端口（读前 flush）；`ArchiveAsync` 前 flush；内层不支持时不暴露端口 |
| `SessionManager` | `ListSummariesAsync` 不填充 `_sessionDataCache`；`EvictAsync` 移除缓存不删盘；`RestoreAsync` |
| `SessionGroupManager` | `ListAnchorSummariesAsync` 不物化组、不写盘；`ArchiveGroupAsync` 整组归档、含子会话；活跃成员保护 |
| 启动扫描 | 超过阈值的组被归档；活跃组不归档；执行中组跳过；失败隔离 |
| WebUI / TUI | 列表走摘要；进入会话加载完整 messages；归档视图列出/恢复 |

---

## 11. 实施顺序建议

1. 端口 + `SessionSummary` + 扩展方法降级（`Seeing.Session` 原语层，含测试）。
2. `FileSessionStore` / `InMemorySessionStore` 实现两端口（含测试）。
3. 写回装饰器转发（含测试）。
4. `SessionManager` / `SessionGroupManager` / `SessionDirectory` 上层 API（含测试）。
5. 启动扫描 `BackgroundService` + `SessionArchivalOptions`（含测试）。
6. WebUI / TUI 接入 + 已归档视图。

---

## 12. 已知边界

| 边界 | 说明 |
|------|------|
| 仅启动扫描 | 运行期间新产生的不活跃会话不会自动归档，直到下次启动 |
| 无独立索引 | 每次列表仍需扫描目录（流式只读顶层字段，比全量反序列化快，但仍是 N 次开文件） |
| 无 LRU 驱逐 | 已加载会话常驻，直到显式 `EvictAsync` 或归档 |
| 执行中判定 | 启动扫描依赖 `ISessionRuntime` 查询在途执行；不可判定时保守跳过（可能漏归档） |
| 组摘要复用 | 组对象小，优先复用 `ISessionGroupStore.ListAsync`；未引入独立组摘要端口 |
| `ISessionCatalog` / `GlobalSessionStore` | 本设计不接线该历史端口；其路径布局债继续保留（未列入本次范围） |
