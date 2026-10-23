# 会话工具模块与通用会话组 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 新增 `session.tools` 能力模块（list/search/read/trim/handoff），并引入通用会话组（`SessionGroup`）作为会话关系的唯一权威，WebUI 改为组驱动窗口。

**Architecture:** 关系（父子/分支/交接）只存 `SessionGroup`，所有关系操作只经 `ISessionGroupManager` 单一入口；`SessionManager` 降级为无关系语义的低层 CRUD；`SessionKind` 去 `Fork`。能力模块只依赖 Abstractions + Seeing.Session + Tools.Support。结束本轮的通用能力经 `ToolResult.TurnDirective` + `AgentExecutor` 消费；组变更经独立 `ISessionGroupEventBus`（非执行流）广播。

**Tech Stack:** .NET 10 / C#、xUnit 2.9 + Moq + FluentAssertions、Microsoft.Extensions.DependencyInjection、Blazor（AntDesign）。

**权威规格：** `docs/superpowers/specs/2026-09-18-session-tools-and-session-groups-design.md`（v7，已终审）。

---

## 0. 执行约定

- 构建：`dotnet build Seeing.Agent.slnx`
- **执行顺序修正（T1 实施后确定）**：`T1 → T4 → T2 → T3 → T5 → T6 → T7`。原因：T1 删除 `ParentSessionId/ForkLabel/Kind.Fork` 会打破同包 `SessionManager`/`SessionForker`；必须紧跟 T4 修复包内编译，再补 T2/T3（新文件，与 T4 无冲突）。T1 单独提交后仓库处于**预期过渡态**。
- 单测（MTP 不可用，一律 VSTest）：
  ```powershell
  dotnet build tests/primitives/Seeing.Session.Tests
  dotnet vstest tests/primitives/Seeing.Session.Tests/bin/Debug/net10.0/Seeing.Session.Tests.dll --TestCaseFilter:"FullyQualifiedName~SessionGroupTests"
  ```
  能力/脊柱/WebUI 同理替换 DLL 路径（见各任务）。
- 命名：`{方法}_{场景}_Should{预期}`。
- 每完成一个任务即时提交（`git add` 指定文件 + `git commit`）。
- 里程碑 M1–M4 之间可并行（见 §2 依赖图）；文件冲突点：`BuiltInCapabilitySets.cs` 仅 M5 改。

## 1. 文件结构（新建/修改一览）

**新建**
| 文件 | 职责 |
|---|---|
| `src/primitives/Seeing.Session/Core/SessionGroup.cs` | 组实体/成员/枚举/EventArgs |
| `src/primitives/Seeing.Session/Core/ISessionGroupManager.cs` | 关系唯一入口契约 |
| `src/primitives/Seeing.Session/Storage/ISessionGroupStore.cs` | 组存储契约（含 `FindBySessionAsync`） |
| `src/primitives/Seeing.Session/Storage/IRelocatableSessionGroupStore.cs` | 工作区重定位 |
| `src/primitives/Seeing.Session/Storage/FileSessionGroupStore.cs` | 文件持久化 |
| `src/primitives/Seeing.Session/Management/SessionGroupManager.cs` | 关系权威实现（两级锁/缓存/物化/级联） |
| `src/abstractions/Seeing.Agent.Abstractions/Events/ISessionGroupEventBus.cs` | 组事件总线契约 + `SessionGroupChangedEvent` |
| `src/spine/Seeing.Agent.Core/Execution/ChannelSessionGroupEventBus.cs` | 总线实现（按 groupId 分发） |
| `src/spine/Seeing.Agent.Core/Hosting/SessionGroupEventBridge.cs` | `IHostedService` 桥：`Changed` → 总线 |
| `src/spine/Seeing.Agent.Core/Configuration/SessionGroupReloadHandler.cs` | 工作区重定位 |
| `src/capabilities/Seeing.Agent.Tools.Session/*` | 模块 + 5 工具 + csproj |
| `samples/Seeing.Agent.WebUI/Services/SessionWindowRegistry.cs` | 组驱动窗口注册表 |
| 测试（见各任务） | |

**修改**：见规格 §11 完整清单。

## 2. 里程碑依赖图

```
M1 原语层 ──┬─→ M2 契约/脊柱 ──┬─→ M3 能力包 ──┐
            └─→ M4 WebUI ──────┘               ├─→ M5 启用/文档/端到端
                                                ┘
```

---

# M1 原语层：会话组

### Task 1: `SessionGroup` 实体与 `SessionKind` 去 Fork

**Files:**
- Create: `src/primitives/Seeing.Session/Core/SessionGroup.cs`
- Modify: `src/primitives/Seeing.Session/Core/SessionKind.cs`
- Modify: `src/primitives/Seeing.Session/Core/SessionData.cs`
- Test: `tests/primitives/Seeing.Session.Tests/Management/SessionGroupTests.cs`

- [ ] **Step 1: 写失败测试（实体基本行为）**

```csharp
using FluentAssertions;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Session.Tests.Management;

public class SessionGroupTests
{
    [Fact]
    public void ResolveActiveId_WhenActiveIsStale_ShouldFallbackToAnchor()
    {
        var group = new SessionGroup
        {
            AnchorSessionId = "a",
            ActiveSessionId = "ghost",
            Members = { new SessionGroupMember { SessionId = "a", IsAnchor = true, Relation = SessionRelation.None } }
        };
        group.ResolveActiveId().Should().Be("a");
    }

    [Fact]
    public void Clone_ShouldDeepCopyMembers()
    {
        var group = new SessionGroup { AnchorSessionId = "a", Members = { new SessionGroupMember { SessionId = "a", IsAnchor = true } } };
        var clone = group.Clone();
        clone.Members[0].SessionId = "x";
        group.Members[0].SessionId.Should().Be("a");
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet build tests/primitives/Seeing.Session.Tests` → FAIL（`SessionGroup` 不存在）

- [ ] **Step 3: 实现实体**

`SessionGroup.cs`：
```csharp
namespace Seeing.Session.Core;

/// <summary>会话关系类型（仅由 SessionGroup 承载）。</summary>
public enum SessionRelation
{
    /// <summary>锚点成员（无外部关系）</summary>
    None = 0,
    /// <summary>子 Agent / Task 会话</summary>
    Child = 1,
    /// <summary>分支会话（含 trim 备份）</summary>
    Fork = 2,
    /// <summary>交接后继</summary>
    HandoffSuccessor = 3
}

public sealed class SessionGroup
{
    public string Id { get; set; } = string.Empty;
    public string PartitionId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string AnchorSessionId { get; set; } = string.Empty;
    public string? ActiveSessionId { get; set; }
    public List<SessionGroupMember> Members { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    /// <summary>每次变更 +1（组锁内），用于订阅方丢弃乱序快照。</summary>
    public long Version { get; set; }

    public string ResolveActiveId() =>
        ActiveSessionId is not null && Members.Any(m => m.SessionId == ActiveSessionId)
            ? ActiveSessionId
            : AnchorSessionId;

    public SessionGroup Clone() => new()
    {
        Id = Id,
        PartitionId = PartitionId,
        Title = Title,
        AnchorSessionId = AnchorSessionId,
        ActiveSessionId = ActiveSessionId,
        Members = Members.Select(m => m.Clone()).ToList(),
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        Version = Version
    };
}

public sealed class SessionGroupMember
{
    public string SessionId { get; set; } = string.Empty;
    public SessionRelation Relation { get; set; }
    public string? ParentSessionId { get; set; }
    public string? Label { get; set; }
    public bool IsAnchor { get; set; }
    public int Order { get; set; }

    public SessionGroupMember Clone() => new()
    {
        SessionId = SessionId,
        Relation = Relation,
        ParentSessionId = ParentSessionId,
        Label = Label,
        IsAnchor = IsAnchor,
        Order = Order
    };
}

public sealed class SessionGroupChangedEventArgs : EventArgs
{
    public required SessionGroup Group { get; init; }
}
```

`SessionKind.cs` 改为：
```csharp
public enum SessionKind
{
    Root = 0,
    SubAgent = 2   // 保留数值 2：删除 Fork 只留空档，避免存量 JSON 数值枚举重编号失效
}
```

`SessionData.cs`：新增 `public string? GroupId { get; set; }`；删除 `ParentSessionId`、`ForkLabel` 两个属性；`Clone()` 中同步删除这两项、加入 `GroupId = GroupId`；`CloneScenarioOverride` 不变。

- [ ] **Step 4: 运行通过 + 全量编译暴露受影响点**

Run: `dotnet build Seeing.Agent.slnx` → 预期出现大量 `ParentSessionId`/`ForkLabel`/`SessionKind.Fork` 编译错误（这是后续任务的工作清单，先记录，本任务只保证原语包自身编译：`dotnet build src/primitives/Seeing.Session`）。

- [ ] **Step 5: 提交**

```bash
git add src/primitives/Seeing.Session/Core/SessionGroup.cs src/primitives/Seeing.Session/Core/SessionKind.cs src/primitives/Seeing.Session/Core/SessionData.cs tests/primitives/Seeing.Session.Tests/Management/SessionGroupTests.cs
git commit -m "feat(session): add SessionGroup entity and drop Kind.Fork"
```

---

### Task 2: `ISessionGroupStore` + `FileSessionGroupStore`

**Files:**
- Create: `src/primitives/Seeing.Session/Storage/ISessionGroupStore.cs`, `IRelocatableSessionGroupStore.cs`, `FileSessionGroupStore.cs`
- Test: `tests/primitives/Seeing.Session.Tests/Storage/FileSessionGroupStoreTests.cs`

- [ ] **Step 1: 写失败测试（往返 + 按会话反查 + 重定位）**

```csharp
[Fact]
public async Task SaveAndLoad_ShouldRoundTrip()
{
    var dir = Directory.CreateTempSubdirectory().FullName;
    var store = new FileSessionGroupStore(dir);
    var g = new SessionGroup { Id = "grp_1", AnchorSessionId = "s1",
        Members = { new SessionGroupMember { SessionId = "s1", IsAnchor = true } } };
    await store.SaveAsync(g);
    var loaded = await store.LoadAsync("grp_1");
    loaded!.AnchorSessionId.Should().Be("s1");
    (await store.FindBySessionAsync("s1"))!.Id.Should().Be("grp_1");
}
```

- [ ] **Step 2: 运行确认失败** → `dotnet build tests/primitives/Seeing.Session.Tests` FAIL

- [ ] **Step 3: 实现**

`ISessionGroupStore.cs`：
```csharp
namespace Seeing.Session.Storage;

public interface ISessionGroupStore
{
    Task<SessionGroup?> LoadAsync(string groupId);
    Task<SessionGroup?> FindBySessionAsync(string sessionId);
    Task SaveAsync(SessionGroup group);
    Task DeleteAsync(string groupId);
    IAsyncEnumerable<SessionGroup> ListAsync();
}
```

`IRelocatableSessionGroupStore.cs`：
```csharp
namespace Seeing.Session.Storage;

public interface IRelocatableSessionGroupStore : ISessionGroupStore
{
    void SetBaseDirectory(string baseDirectory);
}
```

`FileSessionGroupStore.cs`：参照 `Storage/FileSessionStore.cs` 的 JSON 序列化与目录风格实现；文件名 `{groupId}.json`；构造 `FileSessionGroupStore(string? baseDirectory = null)` 默认 `Path.Combine(Environment.GetFolderPath(SpecialFolder.UserProfile), ".seeing", "session-groups")`；内部 `SemaphoreSlim` 写锁；`FindBySessionAsync` 遍历 `ListAsync` 匹配成员。

- [ ] **Step 4: 运行通过** → `dotnet vstest ...Seeing.Session.Tests.dll --TestCaseFilter:"FullyQualifiedName~FileSessionGroupStoreTests"`

- [ ] **Step 5: 提交**

```bash
git add src/primitives/Seeing.Session/Storage/ISessionGroupStore.cs src/primitives/Seeing.Session/Storage/IRelocatableSessionGroupStore.cs src/primitives/Seeing.Session/Storage/FileSessionGroupStore.cs tests/primitives/Seeing.Session.Tests/Storage/FileSessionGroupStoreTests.cs
git commit -m "feat(session): add SessionGroup file store"
```

---

### Task 3: `ISessionGroupManager` + `SessionGroupManager`（创建/物化/两级锁/缓存）

**Files:**
- Create: `src/primitives/Seeing.Session/Core/ISessionGroupManager.cs`
- Create: `src/primitives/Seeing.Session/Management/SessionGroupManager.cs`
- Test: `tests/primitives/Seeing.Session.Tests/Management/SessionGroupManagerTests.cs`

- [ ] **Step 1: 写失败测试**

覆盖：
```csharp
[Fact] public async Task EnsureForSession_ShouldCreateSingleMemberGroup() { ... }
[Fact] public async Task EnsureForSession_Concurrent_ShouldCreateExactlyOneGroup() { ... } // Task.WhenAll 两次，最终同组
[Fact] public async Task SetActive_ShouldUpdateVersionAndRaiseChangedOutsideLock() { ... }
[Fact] public async Task RemoveSession_WhenActive_ShouldFallbackToAnchor() { ... }
[Fact] public async Task RemoveSession_WhenAnchor_ShouldPromoteNextByOrder() { ... }
[Fact] public async Task RemoveSession_ShouldCascadeChildSessions() { ... }
```

- [ ] **Step 2: 运行确认失败**

- [ ] **Step 3: 实现契约**（`ISessionGroupManager.cs`，签名严格照规格 §4.4）

- [ ] **Step 4: 实现管理器**

`SessionGroupManager.cs` 关键点（完整实现）：
- 字段：`ISessionManager _sessions`、`ISessionGroupStore _store`、`ILogger`；`ConcurrentDictionary<string, SessionGroup> _cache`、`ConcurrentDictionary<string, string> _sessionToGroup`、`ConcurrentDictionary<string, SemaphoreSlim> _sessionLocks`、`ConcurrentDictionary<string, SemaphoreSlim> _groupLocks`。
- `EnsureForSessionAsync`：取会话锁；`_sessionToGroup`/`GetGroupForSessionAsync` 命中即返回；否则建单成员组（`Anchor=sessionId, Relation=None, IsAnchor=true, Version=1`）→ `SaveAsync` → `UpdateSessionAsync(sessionId, s => s.GroupId = group.Id)` → 缓存。
- 组内变更（`AddMember/RemoveMember/SetActive/create-*`）：取组锁 → 修改 → `Version++`、`UpdatedAt=now` → `SaveAsync` → 锁内 `group.Clone()` → 释放锁 → `Changed?.Invoke(this, new SessionGroupChangedEventArgs { Group = snapshot })`。
- `RemoveSessionAsync`：取目标会话所属组锁；递归取子成员（`!IsAnchor && Relation==Child && ParentSessionId==id`）；移除成员；active/锚点回退与提升（提升者 `IsAnchor=true, Relation=None`）；组空则 `DeleteAsync` 并广播空成员快照；`ISessionManager.Delete` 逐个删除会话。
- 锁序：如无会话锁则不取；需要"会话→组"时先会话锁后组锁。
- `ListChildrenAsync`：先内存匹配组成员，未命中经 `_store.FindBySessionAsync` 冷兜底后加载组，再 `GetOrLoadAsync` 成员会话。
- `ListAnchorsAsync`：加载分区全部会话（`LoadAllFromStorageAsync`）→ 对 `GroupId==null` 的逐个 `EnsureForSessionAsync` → 返回锚点会话。

- [ ] **Step 5: 运行通过** → `--TestCaseFilter:"FullyQualifiedName~SessionGroupManagerTests"`

- [ ] **Step 6: 提交**

```bash
git add src/primitives/Seeing.Session/Core/ISessionGroupManager.cs src/primitives/Seeing.Session/Management/SessionGroupManager.cs tests/primitives/Seeing.Session.Tests/Management/SessionGroupManagerTests.cs
git commit -m "feat(session): add SessionGroupManager with two-level locking"
```

---

### Task 4: `SessionManager` 降级 + `SessionForker` 纯复制

**Files:**
- Modify: `src/primitives/Seeing.Session/Core/ISessionManager.cs`
- Modify: `src/primitives/Seeing.Session/Management/SessionManager.cs`
- Modify: `src/primitives/Seeing.Session/Management/SessionForker.cs`
- Test: `tests/primitives/Seeing.Session.Tests/Management/SessionChildTests.cs`（整文件重写）

- [ ] **Step 1: 写失败测试**：断言 `ISessionManager` 不再暴露 `ForkAsync/CreateChildAsync/ListChildrenAsync/ListRootsAsync/LoadChildrenFromStorageAsync`（用反射断言接口方法集），并断言 `SessionForker.ForkAsync` 只复制消息、不写 `Kind`/关系。

- [ ] **Step 2: 运行确认失败**

- [ ] **Step 3: 实现**
  - `ISessionManager.cs`：删除这 5 个方法签名（保留 `Create/EnsureSessionAsync/Get/Delete/Register/List/SaveAsync/LoadAsync/AddMessageAsync/ArchiveAsync/ShareAsync/RevertAsync/SetTitle.../GetOrLoadAsync/UpdateSessionAsync/SaveAndNotifyAsync/LoadAllFromStorageAsync`）。`SessionMetadata` 标注 `[Obsolete("关系字段已迁至 SessionGroup；仅遗留全局列表使用")]` 或移除关系字段。
  - `SessionManager.cs`：删除对应实现与 `_forker/_archiver/_sharer/_reverter` 中与关系相关的构造参数（保留 archiver/sharer/reverter 若无其它用途亦一并清理）；`ForkInlineAsync` 删除。
  - `SessionForker.cs`：`ForkAsync(sessionId, label)` ── 仅克隆消息、生成新 Id、`Kind` 由调用方设定（移除内部 `Kind=Root/Fork` 赋值）；不再设置 `ParentSessionId/ForkLabel`。

- [ ] **Step 4: 运行通过**

- [ ] **Step 5: 提交**

```bash
git add src/primitives/Seeing.Session/Core/ISessionManager.cs src/primitives/Seeing.Session/Management/SessionManager.cs src/primitives/Seeing.Session/Management/SessionForker.cs tests/primitives/Seeing.Session.Tests/Management/SessionChildTests.cs
git commit -m "refactor(session): demote SessionManager to relation-free CRUD"
```

---

### Task 5: 关系编排（CreateRoot/CreateChild/Fork/Backup/Handoff）

**Files:**
- Modify: `src/primitives/Seeing.Session/Management/SessionGroupManager.cs`
- Test: `tests/primitives/Seeing.Session.Tests/Management/SessionGroupLifecycleTests.cs`

- [ ] **Step 1: 写失败测试**

```csharp
[Fact] public async Task CreateChild_ShouldJoinParentGroupWithChildRelation() { ... }
[Fact] public async Task Fork_Root_ShouldJoinSourceGroupAsFork() { ... }
[Fact] public async Task Fork_SubAgent_ShouldDetachToNewGroupWithKindRoot() { ... }
[Fact] public async Task Backup_ShouldAlwaysJoinCurrentGroup() { ... }   // 源为 SubAgent 亦然
[Fact] public async Task Handoff_ShouldAddSuccessorAndSetActive() { ... }
```

- [ ] **Step 2: 运行确认失败**

- [ ] **Step 3: 实现**
  - `CreateRootAsync(partitionId, agent, scenario, title, workingDirectory, ct)`：`_sessions.Create(...)` → 设 `WorkingDirectory` → 建组 → 回写 `GroupId` → `SaveAsync`。
  - `CreateChildAsync(parentId, agentName, title, snapshot, scenario, ct)`：`_sessions.Create` → `Kind=SubAgent` → 入父组 `Relation=Child, ParentSessionId=parent`。
  - `ForkSessionAsync(sessionId, title, ct)`：源 `Kind==SubAgent` → 新组（新会话 `Kind=Root, Relation=None, ParentSessionId=源`）；否则 → 源组 `Relation=Fork, ParentSessionId=源`。
  - `CreateBackupForkAsync(sessionId, label, ct)`：始终源组 `Relation=Fork`（与源 `Kind` 无关），`Label=label`。
  - `CreateHandoffSuccessorAsync(sourceId, agent, title, scenario, ct)`：新会话 `Kind=Root` → 源组 `Relation=HandoffSuccessor, ParentSessionId=源` → `SetActive(group, 目标)`。

- [ ] **Step 4: 运行通过**

- [ ] **Step 5: 提交**

```bash
git add src/primitives/Seeing.Session/Management/SessionGroupManager.cs tests/primitives/Seeing.Session.Tests/Management/SessionGroupLifecycleTests.cs
git commit -m "feat(session): add relation orchestration (create/fork/backup/handoff)"
```

---

### Task 6: 迁移既有调用方（编译清零）

**Files:** 按规格 §11「Hosting/Samples」与「修改（原语）」。重点：
- `src/hosting/Seeing.Agent.Hosting/ChatOrchestrator.cs:146-186,189-207,222-237`
- `src/hosting/Seeing.Agent.Hosting/Commands/SessionCommands.cs:34-46`
- `src/hosting/Seeing.Agent.Hosting/Tools/TaskTool.cs:112,115,137,392`、`Tools/TaskStatusTool.cs:131,214`、`Execution/ExecutionJobService.cs:307,1268,1290`
- `src/spine/Seeing.Agent.Core/Services/SessionTitleService.cs:96,166`
- `src/primitives/Seeing.Session/Storage/GlobalSessionStore.cs:125-126`
- `samples/Seeing.Agent.Cli/Commands/SessionCommand.cs:70`
- 记录 `SessionData.Kind` 比较处改为 `SessionKind.Root`（不再有 `Fork`）

- [ ] **Step 1: 逐个替换**
  - `ChatOrchestrator` 注入 `ISessionGroupManager`；`CreateSessionAsync` → `CreateRootAsync(...)`（含 `WorkingDirectory`，随后 `SeedSessionModel` 保留）；`DeleteSessionAsync` → 先 `_executionSubmitter.CancelBySessionAsync(child)` 再 `RemoveSessionAsync`；`BranchSessionAsync` → `ForkSessionAsync`；`ListSessionsAsync` → `ListAnchorsAsync`。
  - `SessionCommands.ForkSession` → `_groupManager.ForkSessionAsync`。
  - `TaskTool`：`CreateChildAsync`（组管理器版）；`session.ParentSessionId` 归属校验 → `await _groupManager.GetParentAsync(session.Id) == context.SessionId`；`ListChildrenAsync` 用组管理器。
  - `ExecutionJobService`/`TaskStatusTool` 的 `Kind` 判断保留（内在行为）；`ListChildrenAsync` 用组管理器。
  - `SessionTitleService`：`kind != Root` 保留；父指针改 `GetParentAsync`。
  - `GlobalSessionStore`：删除 `ParentSessionId/ForkLabel` 读取。
  - `SessionCommand.cs:70`：删除 `ParentSessionId` 使用。

- [ ] **Step 2: 全量编译清零**

Run: `dotnet build Seeing.Agent.slnx` → 0 error（warning 可留）

- [ ] **Step 3: 修复相关单测**（`SessionManagerStorageTests`、`SessionScenarioTests`、`SessionEnhancedTests`、`SessionTitleServiceTests`、`TaskToolTests`、`TaskStatusToolTests`、`AcpToolTests` fake、`Scheduler`）

- [ ] **Step 4: 运行单测**

```powershell
dotnet build tests/spine/Seeing.Agent.Tests
dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll
dotnet build tests/capabilities/Seeing.Agent.Acp.Tests
dotnet vstest tests/capabilities/Seeing.Agent.Acp.Tests/bin/Debug/net10.0/Seeing.Agent.Acp.Tests.dll
```

- [ ] **Step 5: 提交**

```bash
git add -A
git commit -m "refactor: migrate session relation callers to ISessionGroupManager"
```

---

### Task 7: DI 注册（Core + 独立包）

**Files:**
- Modify: `src/spine/Seeing.Agent.Core/Extensions/ServiceCollectionExtensions.cs:438-462`
- Modify: `src/primitives/Seeing.Session/SessionServiceExtensions.cs`
- Test: `tests/spine/Seeing.Agent.Tests/Sessions/SessionGroupDiTests.cs`

- [ ] **Step 1: 写失败测试**：构建 ServiceCollection，`AddSeeingCore` 后断言 `ISessionGroupManager` 与 `SessionForker` 可解析且为 Singleton 单实例。

- [ ] **Step 2: 运行确认失败**

- [ ] **Step 3: 实现**
  - Core `RegisterCoreServices`：注册 `ISessionGroupStore`（`FileSessionGroupStore(Path.Combine(workspace.ProjectSeeingDirectory, "session-groups"))`）、`SessionForker`（`ILogger<SessionForker>` + `ISessionManager`）、`SessionGroupManager` + `ISessionGroupManager`；清理 `SessionManager` 工厂的 `forker: null`。
  - `SessionServiceExtensions.AddSessionManager`：`TryAdd` 语义注册组 store/manager/forker（避免与 Core 双实例）。

- [ ] **Step 4: 运行通过**

- [ ] **Step 5: 提交**

```bash
git add src/spine/Seeing.Agent.Core/Extensions/ServiceCollectionExtensions.cs src/primitives/Seeing.Session/SessionServiceExtensions.cs tests/spine/Seeing.Agent.Tests/Sessions/SessionGroupDiTests.cs
git commit -m "feat(session): register SessionGroup services"
```

---

# M2 契约/脊柱：结束本轮 + 组事件总线

### Task 8: `ToolTurnDirective` + `LoopComplete.Reason` + `AgentExecutor` 退出

**Files:**
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Tools/ToolResult.cs`
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Events/MessageEventTypes.cs`
- Modify: `src/spine/Seeing.Agent.Core/Core/AgentExecutor.cs:476,884,901,459-467`
- Test: `tests/spine/Seeing.Agent.Tests/Execution/EndTurnDirectiveTests.cs`

- [ ] **Step 1: 写失败测试**：一个 fake `ITool` 返回 `TurnDirective=EndTurn`；执行一个含该工具的 agent，断言事件流恰含一次 `LoopCompleteEvent` 且 `Success==true`、`Reason=="test"`，且无第二轮 LLM 调用（fake `ILlmClient` 调用计数 == 1）。

- [ ] **Step 2: 运行确认失败**

- [ ] **Step 3: 实现**
  - `ToolResult`：加 `public ToolTurnDirective TurnDirective { get; set; } = ToolTurnDirective.Continue;` + `public string? TurnDirectiveReason { get; set; }`；文件顶部加 `public enum ToolTurnDirective { Continue = 0, EndTurn = 1 }`。
  - `ToolCallEvent`：加 `public ToolTurnDirective TurnDirective { get; init; } = ToolTurnDirective.Continue;` + `public string? TurnDirectiveReason { get; init; }`。
  - `LoopCompleteEvent`（`:156`）：加 `public string? Reason { get; init; }`。
  - `AgentExecutor.ExecuteSingleToolCallAsync`：成功分支（`:901`）与失败分支（`:884`）构造 `ToolCallEvent` 时复制 `result.TurnDirective/TurnDirectiveReason`。
  - `ExecuteToolCallsAsync`：改为返回 `Task<bool>`；`ForwardToolEventsAsync` 增加 `out bool endTurn`（或用 `ref`/闭包标志），任一 `ToolCallEvent.TurnDirective==EndTurn` 置真。
  - `RunProducerAsync:476`：`var endTurn = await ExecuteToolCallsAsync(...); if (endTurn) { 写 LoopCompleteEvent{Success=true, Reason=...}; return; }`。

- [ ] **Step 4: 运行通过** → `--TestCaseFilter:"FullyQualifiedName~EndTurnDirectiveTests"`

- [ ] **Step 5: 提交**

```bash
git add src/abstractions/Seeing.Agent.Abstractions/Tools/ToolResult.cs src/abstractions/Seeing.Agent.Abstractions/Events/MessageEventTypes.cs src/spine/Seeing.Agent.Core/Core/AgentExecutor.cs tests/spine/Seeing.Agent.Tests/Execution/EndTurnDirectiveTests.cs
git commit -m "feat(core): support tool-requested turn termination"
```

---

### Task 9: `ISessionGroupEventBus` + `ChannelSessionGroupEventBus` + Bridge

**Files:**
- Create: `src/abstractions/Seeing.Agent.Abstractions/Events/ISessionGroupEventBus.cs`
- Create: `src/spine/Seeing.Agent.Core/Execution/ChannelSessionGroupEventBus.cs`
- Create: `src/spine/Seeing.Agent.Core/Hosting/SessionGroupEventBridge.cs`
- Modify: `src/spine/Seeing.Agent.Core/Extensions/ServiceCollectionExtensions.cs`
- Test: `tests/spine/Seeing.Agent.Tests/Sessions/SessionGroupEventBusTests.cs`

- [ ] **Step 1: 写失败测试**
  - 总线：订阅 groupId 后 `Publish` 收到；无订阅者时 Publish 不抛。
  - 桥：构造 fake `ISessionGroupManager`，触发 `Changed`，断言总线订阅者收到 `SessionGroupChangedEvent` 且 `Version` 一致；`StopAsync` 后退订不再收到。

- [ ] **Step 2: 运行确认失败**

- [ ] **Step 3: 实现**
  - `ISessionGroupEventBus.cs`（照规格 §5.3 定义 `SessionGroupChangedEvent` 为普通 record，**非 `IMessageEvent`**）。
  - `ChannelSessionGroupEventBus.cs`：`ConcurrentDictionary<string, List<Channel<SessionGroupChangedEvent>>>`；`Publish` 向该 groupId 所有 channel `TryWrite`；`SubscribeAsync` 建 channel 并注册，`finally` 注销。
  - `SessionGroupEventBridge : IHostedService`：`StartAsync` 订阅 `_groups.Changed`（handler：`_bus.Publish(new SessionGroupChangedEvent{...})`）；`StopAsync` 退订。
  - DI：`AddSingleton<ISessionGroupEventBus, ChannelSessionGroupEventBus>()` + `AddHostedService<SessionGroupEventBridge>()`。

- [ ] **Step 4: 运行通过**

- [ ] **Step 5: 提交**

```bash
git add src/abstractions/Seeing.Agent.Abstractions/Events/ISessionGroupEventBus.cs src/spine/Seeing.Agent.Core/Execution/ChannelSessionGroupEventBus.cs src/spine/Seeing.Agent.Core/Hosting/SessionGroupEventBridge.cs src/spine/Seeing.Agent.Core/Extensions/ServiceCollectionExtensions.cs tests/spine/Seeing.Agent.Tests/Sessions/SessionGroupEventBusTests.cs
git commit -m "feat(core): add session group event bus and bridge"
```

---

### Task 10: `SessionGroupReloadHandler`

**Files:**
- Create: `src/spine/Seeing.Agent.Core/Configuration/SessionGroupReloadHandler.cs`
- Modify: `src/spine/Seeing.Agent.Core/Configuration/SessionReloadHandler.cs`（如注册在 core 处）
- Test: `tests/spine/Seeing.Agent.Tests/Configuration/SessionGroupReloadHandlerTests.cs`

- [ ] **Step 1–4:** 照 `SessionReloadHandler.cs:30-41` 实现：
  - 类 `SessionGroupReloadHandler : ReloadHandlerBase<WorkspaceChange>`；构造注入 `ISessionGroupStore`（须实现 `IRelocatableSessionGroupStore`）。
  - `ComponentId => "session.groups"`。
  - `ReloadAsync`：`if (_store is IRelocatableSessionGroupStore r && !string.IsNullOrEmpty(change.NewWorkspace)) r.SetBaseDirectory(Path.Combine(change.NewWorkspace, ".seeing", "session-groups"));`
  - 测试：写入组到工作区 A → 触发 `WorkspaceChange(NewWorkspace=B)` → `LoadAsync` 从 B 目录读到（A 目录写入不被读到）。

- [ ] **Step 5: 提交** `git commit -m "feat(core): relocate session-group store on workspace switch"`

---

# M3 能力包 `session.tools`

### Task 11: 包骨架 + 模块

**Files:**
- Create: `src/capabilities/Seeing.Agent.Tools.Session/Seeing.Agent.Tools.Session.csproj`
- Create: `src/capabilities/Seeing.Agent.Tools.Session/SessionToolsModule.cs`
- Modify: `Seeing.Agent.slnx`（在既有 `/src/capabilities/` Folder 内追加）
- Test: `tests/capabilities/Seeing.Agent.Tools.Session.Tests/Seeing.Agent.Tools.Session.Tests.csproj` + `SessionToolsModuleTests.cs`

- [ ] **Step 1: 写失败测试**：`SessionToolsModule.Id == "session.tools"`；`ProvidedTools` 含 5 个 id；无 `IExecutionSubmitter` 时 `ActivateAsync` 不注册 `session_handoff`（mock `IToolManager`）。

- [ ] **Step 2: 运行确认失败**

- [ ] **Step 3: 实现** `csproj` 引用 `Seeing.Agent.Abstractions`、`Seeing.Session`、`Seeing.Agent.Tools.Support`；模块照 `Tools.Basic/BasicModule.cs` 模式；5 工具 `AddSingleton`，`ActivateAsync` 逐个 `RegisterToolAsync`，`DeactivateAsync` 反注册。

- [ ] **Step 4–5: 运行通过 + 提交**

```bash
git commit -m "feat(session-tools): scaffold module"
```

---

### Task 12: `session_list`

**Files:** Create `SessionListTool.cs`; Test `SessionListToolTests.cs`

- [ ] **Step 1: 写失败测试**：related 返回组成员行（`session_id | relation | kind | title | updated_at | message_count`）；`limit` 截断带 `offset=...` 提示；`scope=all` 触发权限（mock `IPermissionAuthorizerFactory` 返回 Deny → 工具 Failure）。

- [ ] **Step 2–4:** 实现：注入 `ISessionGroupManager` + `ISessionManager`；`related` 走 `GetGroupForSessionAsync` + `ListMembersAsync` + `GetOrLoadAsync`；`all` 走 `ListAnchorsAsync`/`LoadAllFromStorageAsync`；参数 `GetStringArgument/GetIntArgument`；输出行构造与截断。

- [ ] **Step 5: 提交** `git commit -m "feat(session-tools): add session_list"`

---

### Task 13: `session_search`（对齐 grep）

**Files:** Create `SessionSearchTool.cs`; Test `SessionSearchToolTests.cs`

- [ ] **Step 1: 写失败测试**：
  - 命中正则的消息按 `<message_id>:<role>:step<n>:<preview>` 返回；
  - `limit` 截断且末尾含 `offset=` 提示；
  - 输出**不含** `ReasoningContent`；
  - 跨组会话触发审批。

- [ ] **Step 2–4:** 实现：`[ToolCapability(ToolCapabilityKeys.OutputSkip,"true")]`；参数 `session_id/pattern/role/context/limit/offset`；遍历 `GetActiveMessages()`（`include_compacted` 不在本工具）；`Regex` 校验；单条预览 `max_chars`（默认 200）；Metadata `matches/returned/truncated/next_offset`。

- [ ] **Step 5: 提交**

---

### Task 14: `session_read`（对齐 read）

**Files:** Create `SessionReadTool.cs`; Test `SessionReadToolTests.cs`

- [ ] **Step 1: 写失败测试**：
  - `offset`+`limit` 分页；`from_id`+`to_id` 闭区间；`message_id`+`before`+`after` 窗口；
  - 单条超 `max_chars` 截断；
  - 默认不含思考内容；`include_tool_output=false` 时工具结果仅摘要；
  - 尾部含续读提示。

- [ ] **Step 2–4:** 实现（完整逻辑）：
  1. 解析定位三选一：`from_id`+`to_id` → 取 `Messages` 中两 id 的索引闭区间；`message_id`+`before`+`after` → 以该消息索引为中心向两侧扩；否则 `offset`+`limit` 分页。
  2. 取消息源：默认 `GetActiveMessages()`；`include_compacted=true` 时用 `Messages`。
  3. 逐条构造 `[#<idx> | <message_id> | <role> | step=<n> | <time>]` + 内容；`Content` 超 `max_chars` 截断并附 `…[截断 N 字符]`；**永不输出 `ReasoningContent`**。
  4. `include_tool_output=false` 时，`ToolCalls` 仅渲染 `tool: <name>(<status>)`（无 `Result` 全文）。
  5. 尾部输出 `(显示 a-b/共 N 条；续读 from_id=<下一 id>)`；Metadata `total/from_index/to_index/truncated/next_cursor`。

- [ ] **Step 5: 提交**

---

### Task 15: `session_trim`

**Files:** Create `SessionTrimTool.cs`; Test `SessionTrimToolTests.cs`

- [ ] **Step 1: 写失败测试**
  - `before` 删活跃前缀；`range` 删闭区间；
  - 最后一条摘要**不可裁**（返回 Failure 说明）；
  - 最后一条 `user` 及其之后**不可裁**；
  - 自动备份：调用后存在 `Relation=Fork` 的备份会话，`backup_session_id` 返回；
  - 已压缩历史**保留**（`Messages` 中摘要前消息仍在）；
  - 备份失败 → fail-closed（mock `CreateBackupForkAsync` 抛异常，断言未删除）。

- [ ] **Step 2–4:** 实现：`active = session.GetActiveMessages()`；`floorIndex = 最后 user 的索引`；可裁集 = active 中去掉 `IsSummary` 边界与该 user 及之后；按 mode 求交；`CreateBackupForkAsync`；`UpdateSessionAsync(s => s.ReplaceMessages(s.Messages.Where(m => !targetIds.Contains(m.Id))))`；返回计数。

- [ ] **Step 5: 提交**

---

### Task 16: `session_handoff`

**Files:** Create `SessionHandoffTool.cs`; Test `SessionHandoffToolTests.cs`

- [ ] **Step 1: 写失败测试**
  - 成功：目标会话新建、入源组 `HandoffSuccessor`、组 active=目标、目标首条 user=prompt、`SubmitAsync` 被调用（`SkipUserMessagePersist=true`）、返回 `TurnDirective=EndTurn`；
  - 失败：`SubmitAsync` 返回失败 → 目标被删、成员被移除、active 还原、返回 Failure。

- [ ] **Step 2–4:** 实现；能力声明 `TimeoutSkip=true` + `CacheEnabled=false`；参照 `TaskTool.cs:159-187`。

- [ ] **Step 5: 提交**

---

# M4 WebUI 组驱动窗口

### Task 17: `SessionWindowRegistry`

**Files:** Create `samples/Seeing.Agent.WebUI/Services/SessionWindowRegistry.cs`; Modify `Program.cs`（注册 consumer）；Test `tests/apps/Seeing.Agent.WebUI.Tests/Services/SessionWindowRegistryTests.cs`

- [ ] **Step 1: 写失败测试**：`Rebind(sessionId)` 后 `Windows` 含组内成员与 `Relation`；收到 `SessionGroupChangedEvent` 后 active/集合更新；`Version` 旧的快照被丢弃；`Dispose` 取消总线订阅。

- [ ] **Step 2–4:** 实现 `IStreamConsumer`+`IDisposable`；`Rebind` **先订阅总线再枚举**；按 groupId 订阅；窗口节点含 `IsActive/IsExecuting`（执行态由各窗口 handler 提供）。

- [ ] **Step 5: 提交**

---

### Task 18: 页面与 `SessionWindow`/`SessionToolbar` 能力标记

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Services/SessionWindowContext.cs:28`
- Modify: `samples/Seeing.Agent.WebUI/Pages/Session.razor`、`Pages/Conference.razor`
- Modify: `samples/Seeing.Agent.WebUI/Components/SessionWindow.razor`、`Components/SessionToolbar.razor`
- Modify: `samples/Seeing.Agent.WebUI/Services/TaskSessionResolver.cs:35,42`
- Delete: `Services/ConferenceRegistry.cs`；迁移测试 `ConferenceRegistryTests.cs` → `SessionWindowRegistryTests.cs`

- [ ] **Step 1: 写失败测试**：能力标记映射（`IsReadOnly/CanDetach/CanBranch/CanCreateSession/ShowReturnToParent` 等）按 `Relation`/`Kind` 正确；子代理只读行为保持。

- [ ] **Step 2–4:** 按规格 §7.2 映射表逐行替换（`SessionWindow:106,142,228,797,990,1243,1249,1264,1298,1305-1315`；`SessionToolbar:43,57,70,97,110,135,149,151,160,174`；`Session.razor:104,129`）；`OnTileClick` → `SetActiveAsync`；`ParentSessionId` 读取 → `GetParentAsync`；删除 `ConferenceRegistry`。

- [ ] **Step 5: 提交** `git commit -m "refactor(webui): group-driven session windows"`

---

# M5 启用/文档/端到端

### Task 19: 能力集与示例注册

**Files:** Modify `BuiltInCapabilitySets.cs`（`full` 加 `session.tools`，建议 `code`）；`WebUI/Program.cs`、`Cli/Infrastructure/CliServiceBootstrap.cs`；`BuiltInScenariosTests.cs:46-69`。

- [ ] **Step 1–4:** 更新常量 + 注册模块；运行 `BuiltInScenariosTests`。
- [ ] **Step 5: 提交**

### Task 20: 文档

**Files:** `docs/architecture/05-extension-guide.md`（能力包可引原语）、`02-modules.md`（登记 `Seeing.Agent.Tools.Session | session.tools`）、`06-compliance-audit.md`（复扫纳入）。
- [ ] 更新并在 §11 扫描命令下验证无意外命中；提交。

### Task 21: 端到端验证

- [ ] `dotnet build Seeing.Agent.slnx` → 0 error
- [ ] 运行各测试 DLL（原语/脊柱/能力/WebUI/Acp/Scheduler）
- [ ] 启动 `dotnet run --project samples/Seeing.Agent.WebUI`，手动验证：会话 A 触发 `session_handoff` → 自动切到新会话 B，A 成为侧栏小窗；点击互切；`session_trim` 后备份 fork 可见；`session_search/read` 有界。
- [ ] 提交 `git commit -m "docs+chore: enable session.tools and finalize"`

---

## 3. 自检清单（计划对规格覆盖）

- [x] SessionGroup 单一权威/单入口/无旁路 → M1 Task 1–7
- [x] Kind 去关系语义（保留 SubAgent=2）→ Task 1
- [x] 两级锁/Version/锁内克隆 → Task 3、Task 9
- [x] 5 工具有界/不返回思考/工具摘要 → M3 Task 12–16
- [x] EndTurn 重构 + 装饰器语义 → Task 8
- [x] 组事件总线（非执行流）→ Task 9
- [x] WebUI 组驱动 + 能力标记 → M4 Task 17–18
- [x] 删除 ConferenceRegistry → Task 18
- [x] 启用/文档/端到端 → M5
- [x] 编译面迁移清单 → Task 6（规格 §11）
