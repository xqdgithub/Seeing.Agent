# 会话关系树与关系展示 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让会话组关系（主线/已交接/交接后继/分叉/备份/子代理）语义正确，并在 WebUI 以"顶部关系条 + 关系抽屉 + 侧栏状态分层"正确呈现，交接后锚点后移并自动导航。

**Architecture:** 原语层新增 `SessionRelation.HandoffPredecessor` 与**唯一锚点归一函数** `NormalizeAnchor`（内存态、调用方统一持久化）；`ParentSessionId` 统一为"来源"。宿主/脊柱修正交接前置校验、标题生成、删除取消。WebUI 以**组快照**为关系数据源（顶条/抽屉）、以注册表 `WindowNode` 为侧栏骨架，新增两个组件并保留既有卡片功能。

**Tech Stack:** .NET 10 / C#、xUnit 2.9 + Moq + FluentAssertions、Blazor Server + AntDesign Blazor、VSTest（MTP 不可用）。

**Spec:** `docs/superpowers/specs/2026-09-18-session-relation-tree-and-display-design.md`

---

## 执行策略（并行优先 + 关键点审查）

- **并行**：按 Stream 分派子 Agent；同一文件只由一个 Stream 负责，避免写冲突。
- **审查仅 3 个关键闸口**：
  - **Gate 1（必审）**：原语层归一/删除/交接原子（Tasks 2–4）——正确性核心。
  - **Gate 2（必审）**：WebUI 组件与状态分层（Tasks 9–13）。
  - **Gate 3（轻审）**：接线与文档（Tasks 14–15）+ 全量回归。
- **提交**：每个任务末尾一次小提交（若用户要求批量提交则合并）。

**测试命令约定（VSTest，禁止用 `dotnet test`）**
```bash
dotnet build tests/<路径>
dotnet vstest tests/<路径>/bin/Debug/net10.0/<程序集>.dll --TestCaseFilter:"FullyQualifiedName~<类名>"
```

---

## File Structure

**原语层**
- Modify `src/primitives/Seeing.Session/Core/SessionGroup.cs`：`SessionRelation` 新增 `HandoffPredecessor`。
- Modify `src/primitives/Seeing.Session/Management/SessionGroupManager.cs`：`NormalizeAnchor`（新）、`ValidateMemberInvariant`、`AddMemberAsync`、`RemoveMemberAsync`、`CreateHandoffSuccessorAsync`、`PromoteAnchor`（删除/重命名）。
- Modify `src/primitives/Seeing.Session/Core/ISessionGroupManager.cs`：新增 `SetAnchorAsync`（可选，供测试/宿主；内部仍归一路径）。

**脊柱 / 宿主**
- Modify `src/spine/Seeing.Agent.Core/Services/SessionTitleService.cs`：`ShouldEnsure` 改按"是否 Child"判定。
- Modify `src/hosting/Seeing.Agent.Hosting/ChatOrchestrator.cs`：删除会话时取消其**自身**在途执行。
- Modify `src/capabilities/Seeing.Agent.Tools.Session/SessionHandoffTool.cs`：加"源必须为当前锚点"校验。

**WebUI**
- Modify `samples/Seeing.Agent.WebUI/Services/SessionWindowRegistry.cs`：`WindowNode` 增加 `IsAnchor`/`Label`；分区排序投影。
- Create `samples/Seeing.Agent.WebUI/Services/SessionRelationView.cs`：关系/状态→文案/颜色/分区 的纯映射（供三处复用）。
- Modify `samples/Seeing.Agent.WebUI/Services/SessionWindowContext.cs`：暴露 `Relation`。
- Modify `samples/Seeing.Agent.WebUI/Components/SessionWindow.razor`：Summary 状态分层、关系徽标、来源行、执行态回调。
- Modify `samples/Seeing.Agent.WebUI/Components/SessionToolbar.razor`：徽标按 relation。
- Create `samples/Seeing.Agent.WebUI/Components/SessionRelationBar.razor`：顶部关系条。
- Create `samples/Seeing.Agent.WebUI/Components/SessionRelationDrawer.razor`：关系抽屉。
- Modify `samples/Seeing.Agent.WebUI/Pages/Session.razor`：接线顶条/抽屉/侧栏分区 + 交接导航。
- Modify `samples/Seeing.Agent.WebUI/Pages/Conference.razor`：接线顶条/抽屉（大屏保留两列）。

**测试**
- Modify/Add `tests/primitives/Seeing.Session.Tests/Management/SessionGroupManagerTests.cs`、`SessionGroupLifecycleTests.cs`
- Add `tests/primitives/Seeing.Session.Tests/Management/SessionGroupAnchorNormalizationTests.cs`
- Modify `tests/spine/Seeing.Agent.Tests/Services/SessionTitleServiceTests.cs`
- Modify `tests/spine/Seeing.Agent.Tests/App/ChatOrchestratorCreateSessionTests.cs`（或新增删除用例）
- Modify `tests/capabilities/Seeing.Agent.Tools.Session.Tests/SessionHandoffToolTests.cs`
- Modify/Add `tests/apps/Seeing.Agent.WebUI.Tests/Services/SessionWindowRegistryTests.cs`、`SessionRelationViewTests.cs`
- Add `tests/apps/Seeing.Agent.WebUI.Tests/Components/SessionRelationBarTests.cs`、`SessionRelationDrawerTests.cs`

**文档**
- Modify `docs/architecture/09-session-tools-and-groups-release-notes.md`
- Modify `src/primitives/Seeing.Session/Management/SessionGroupManager.cs`（XML 注释）
- 追注 `docs/superpowers/specs/2026-09-18-session-tools-and-session-groups-design.md`（旧"锚点必须 None"）

---

## Wave 0 — 基础枚举（串行，解阻并行）

### Task 1: 新增 `SessionRelation.HandoffPredecessor`

**Files:**
- Modify: `src/primitives/Seeing.Session/Core/SessionGroup.cs:6-16`
- Test: `tests/primitives/Seeing.Session.Tests/Management/SessionGroupTests.cs`

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void SessionRelation_HandoffPredecessor_ShouldBeFour()
{
    ((int)SessionRelation.HandoffPredecessor).Should().Be(4);
}
```

- [ ] **Step 2: 运行确认失败**

```bash
dotnet build tests/primitives/Seeing.Session.Tests
dotnet vstest tests/primitives/Seeing.Session.Tests/bin/Debug/net10.0/Seeing.Session.Tests.dll --TestCaseFilter:"FullyQualifiedName~SessionGroupTests"
```
Expected: 编译失败（`HandoffPredecessor` 未定义）。

- [ ] **Step 3: 实现**

```csharp
public enum SessionRelation
{
    None = 0,
    Child = 1,
    Fork = 2,
    HandoffSuccessor = 3,
    /// <summary>已被后续交接取代的主线节点</summary>
    HandoffPredecessor = 4
}
```

- [ ] **Step 4: 运行确认通过**（同 Step 2，Expected: PASS）
- [ ] **Step 5: 提交** `git add ... && git commit -m "feat(session): add HandoffPredecessor relation"`

---

## Wave 1 — 三个并行 Stream

### Stream A（原语层，关键路径，Gate 1）

#### Task 2: `NormalizeAnchor` 唯一归一（内存态）

**Files:**
- Modify: `src/primitives/Seeing.Session/Management/SessionGroupManager.cs`（替换 `PromoteAnchor`）
- Add: `tests/primitives/Seeing.Session.Tests/Management/SessionGroupAnchorNormalizationTests.cs`

- [ ] **Step 1: 写失败测试**

```csharp
public class SessionGroupAnchorNormalizationTests
{
    [Fact]
    public async Task Handoff_ShouldPromoteSuccessorAsAnchor_WithSuccessorRelation()
    {
        using var h = new SessionGroupTestHarness();
        var root = h.CreateRoot("根");
        var group = await h.Manager.EnsureForSessionAsync(root.Id);

        var successor = await h.Manager.CreateHandoffSuccessorAsync(root.Id, null, "后继", null);

        var g = await h.Manager.GetGroupAsync(group.Id);
        var anchor = g!.Members.Single(m => m.IsAnchor);
        anchor.SessionId.Should().Be(successor.Id);
        anchor.Relation.Should().Be(SessionRelation.HandoffSuccessor);
        g.Members.Single(m => m.SessionId == root.Id).Relation
            .Should().Be(SessionRelation.HandoffPredecessor);
        g.AnchorSessionId.Should().Be(successor.Id);
        g.ActiveSessionId.Should().Be(successor.Id);
    }

    [Fact]
    public async Task DeleteFork_ShouldKeepAnchorUnchanged()
    {
        using var h = new SessionGroupTestHarness();
        var root = h.CreateRoot("根");
        var group = await h.Manager.EnsureForSessionAsync(root.Id);
        var backup = await h.Manager.CreateBackupForkAsync(root.Id, "trim-backup t0");
        var anchorBefore = (await h.Manager.GetGroupAsync(group.Id))!.AnchorSessionId;

        await h.Manager.RemoveMemberAsync(group.Id, backup.Id);

        (await h.Manager.GetGroupAsync(group.Id))!.AnchorSessionId.Should().Be(anchorBefore);
    }
}
```

- [ ] **Step 2: 运行确认失败**（`FullyQualifiedName~SessionGroupAnchorNormalizationTests`）
- [ ] **Step 3: 实现（替换 `PromoteAnchor`，`SessionGroupManager.cs:684-703`）**

```csharp
/// <summary>
/// 锚点归一（仅内存态；不持久化/不发布，由调用方统一 Touch/SaveAsync/RaiseChanged）。
/// 优先级：(a) hint → (b) 当前锚点存活保持 → (c) 主线链回溯 → (d) 非 Fork 且非 Child
/// → (e) Fork 兜底 → (f) 仅剩 Child 退化。
/// </summary>
private static void NormalizeAnchor(SessionGroup group, string? preferredMemberHint = null)
{
    if (group.Members.Count == 0) return;

    SessionGroupMember? ByOrder(Func<SessionGroupMember, bool> pred) =>
        group.Members.Where(pred).OrderBy(m => m.Order).FirstOrDefault();

    SessionGroupMember? target = null;
    if (!string.IsNullOrEmpty(preferredMemberHint))
        target = group.Members.FirstOrDefault(m => m.SessionId == preferredMemberHint);

    if (target is null)
    {
        var current = group.Members.FirstOrDefault(m => m.IsAnchor)
            ?? group.Members.FirstOrDefault(m => m.SessionId == group.AnchorSessionId);
        if (current is not null) target = current;            // (b) 幂等
    }

    if (target is null && !string.IsNullOrEmpty(group.AnchorSessionId))
    {
        var byId = group.Members.ToDictionary(m => m.SessionId, StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        if (byId.TryGetValue(group.AnchorSessionId, out var cursor))
        {
            while (!string.IsNullOrEmpty(cursor.ParentSessionId)
                   && byId.TryGetValue(cursor.ParentSessionId!, out var parent)
                   && visited.Add(parent.SessionId))
                cursor = parent;
            target = cursor;                                   // (c)
        }
    }

    target ??= ByOrder(m => m.Relation != SessionRelation.Fork && m.Relation != SessionRelation.Child); // (d)
    target ??= ByOrder(m => m.Relation == SessionRelation.Fork);                                          // (e)
    target ??= ByOrder(_ => true);                                                                        // (f)
    if (target is null) return;

    foreach (var m in group.Members) m.IsAnchor = false;
    target.IsAnchor = true;
    target.Relation = target.Relation == SessionRelation.Child
        ? SessionRelation.Child
        : (target.Relation == SessionRelation.Fork || string.IsNullOrEmpty(target.ParentSessionId)
            ? SessionRelation.None
            : SessionRelation.HandoffSuccessor);

    group.AnchorSessionId = target.SessionId;
}
```

> 调用方负责 `group.Title = _sessions.Get(group.AnchorSessionId)?.Title ?? group.Title;` 与持久化。

- [ ] **Step 4: 运行确认通过**
- [ ] **Step 5: 提交**

#### Task 3: 交接原子迁移（组锁内）

**Files:**
- Modify: `src/primitives/Seeing.Session/Management/SessionGroupManager.cs:570-616`（`CreateHandoffSuccessorAsync`）

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public async Task Handoff_SourceNotAnchor_ShouldThrow()
{
    using var h = new SessionGroupTestHarness();
    var root = h.CreateRoot();
    var group = await h.Manager.EnsureForSessionAsync(root.Id);
    await h.Manager.CreateHandoffSuccessorAsync(root.Id, null, "后继", null); // root 变为 Predecessor

    var act = async () => await h.Manager.CreateHandoffSuccessorAsync(root.Id, null, "再次", null);
    await act.Should().ThrowAsync<InvalidOperationException>(); // 源必须为当前锚点
}

[Fact]
public async Task Handoff_ShouldNotPublishIntermediateSnapshot()
{
    using var h = new SessionGroupTestHarness();
    var root = h.CreateRoot();
    var group = await h.Manager.EnsureForSessionAsync(root.Id);
    var versions = new List<long>();
    h.Manager.Changed += (_, e) => versions.Add(e.Group.Version);

    var successor = await h.Manager.CreateHandoffSuccessorAsync(root.Id, null, "后继", null);

    versions.Should().HaveCount(1);                       // 仅一次发布
    versions[0].Should().Be(2);                           // 单次 +1（EnsureForSession=1）
    var g = await h.Manager.GetGroupAsync(group.Id);
    g!.Members.Single(m => m.IsAnchor).SessionId.Should().Be(successor.Id);
}
```

- [ ] **Step 2: 运行确认失败**
- [ ] **Step 3: 实现**：将 §3.1 逻辑放入**单个组锁段**，使用内部 helper（勿调用公开 `AddMemberAsync`/`SetActiveAsync`，`SemaphoreSlim` 不可重入）：

```csharp
// CreateHandoffSuccessorAsync 内（组锁内）：
var group = await GetGroupLiveAsync(group.Id, ct).ConfigureAwait(false)
    ?? throw new InvalidOperationException($"Session group not found: {group.Id}");

var sourceMember = group.Members.FirstOrDefault(m => m.SessionId == sourceSessionId)
    ?? throw new InvalidOperationException($"会话不在组内: {sourceSessionId}");
if (!(sourceMember.IsAnchor || group.AnchorSessionId == sourceSessionId))
    throw new InvalidOperationException("仅当前锚点可被交接");

sourceMember.IsAnchor = false;
sourceMember.Relation = SessionRelation.HandoffPredecessor;

var successorMember = new SessionGroupMember
{
    SessionId = successor.Id,
    Relation = SessionRelation.HandoffSuccessor,
    ParentSessionId = sourceSessionId,
    Order = group.Members.Count == 0 ? 0 : group.Members.Max(m => m.Order) + 1
};
group.Members.Add(successorMember);
_sessionToGroup[successor.Id] = group.Id;
group.ActiveSessionId = successor.Id;

NormalizeAnchor(group, successor.Id);
group.Title = _sessions.Get(group.AnchorSessionId)?.Title ?? group.Title;
Touch(group);
await _store.SaveAsync(group).ConfigureAwait(false);
snapshot = group.Clone();
// 锁外：RaiseChanged(snapshot)
```
保留失败自清理（`CleanupHandoffSuccessorAsync`）。移除原 `AddMemberAsync` + `SetActiveAsync` 两次调用。

- [ ] **Step 4: 运行确认通过**
- [ ] **Step 5: 提交**

#### Task 4: 关系感知删除（重挂 + 全路径归一）

**Files:**
- Modify: `src/primitives/Seeing.Session/Management/SessionGroupManager.cs:186-229`（`RemoveMemberAsync`）

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public async Task DeleteHeadNode_ShouldReparentSuccessorAndNormalizeAnchor()
{
    using var h = new SessionGroupTestHarness();
    var a = h.CreateRoot("A");
    var group = await h.Manager.EnsureForSessionAsync(a.Id);
    var b = await h.Manager.CreateHandoffSuccessorAsync(a.Id, null, "B", null); // A→B, B 锚点

    await h.Manager.RemoveMemberAsync(group.Id, a.Id);

    var g = await h.Manager.GetGroupAsync(group.Id);
    var bMember = g!.Members.Single(m => m.SessionId == b.Id);
    bMember.ParentSessionId.Should().BeNull();
    bMember.IsAnchor.Should().BeTrue();
    bMember.Relation.Should().Be(SessionRelation.None);   // 删链首后 parent=null → 归一为 None
    g.AnchorSessionId.Should().Be(b.Id);
}

[Fact]
public async Task DeleteMiddleNode_ShouldReparentSuccessorOnly_NotForkOrChild()
{
    using var h = new SessionGroupTestHarness();
    var a = h.CreateRoot("A");
    var group = await h.Manager.EnsureForSessionAsync(a.Id);
    var b = await h.Manager.CreateHandoffSuccessorAsync(a.Id, null, "B", null);
    var c = await h.Manager.CreateHandoffSuccessorAsync(b.Id, null, "C", null); // A→B→C
    var fork = await h.Manager.CreateBackupForkAsync(b.Id, "trim-backup t1");

    await h.Manager.RemoveMemberAsync(group.Id, b.Id);

    var g = await h.Manager.GetGroupAsync(group.Id);
    g!.Members.Single(m => m.SessionId == c.Id).ParentSessionId.Should().Be(a.Id); // 主线后继重挂
    g.Members.Single(m => m.SessionId == fork.Id).ParentSessionId.Should().Be(a.Id); // 派生重挂
    g.Members.Single(m => m.IsAnchor).SessionId.Should().Be(c.Id);                    // 锚点保持
}
```

- [ ] **Step 2: 运行确认失败**
- [ ] **Step 3: 实现**（在移除前重挂，移除后无条件归一）

```csharp
var wasAnchor = member.IsAnchor || group.AnchorSessionId == sessionId;
var deletedParent = member.ParentSessionId;

// 重挂：主线后继 + Fork/Child 的来源指向被删节点 → 改指其父
foreach (var m in group.Members)
{
    if (m.SessionId == sessionId) continue;
    if (!string.Equals(m.ParentSessionId, sessionId, StringComparison.Ordinal)) continue;
    m.ParentSessionId = deletedParent; // 可能为 null
}

group.Members.Remove(member);
UnmapSession(sessionId, group.Id);
if (group.ActiveSessionId == sessionId) group.ActiveSessionId = null;

// 全路径归一：优先回溯到被删节点的父
NormalizeAnchor(group, wasAnchor ? deletedParent : null);
group.Title = _sessions.Get(group.AnchorSessionId)?.Title ?? group.Title;
Touch(group);
// 空组删除 / 否则 SaveAsync（保留原分支）
```
将原 `if (wasAnchor) PromoteAnchor(group);` 替换为上述无条件归一（`NormalizeAnchor` 幂等）。

- [ ] **Step 4: 运行确认通过**（含既有 `SessionGroupManagerTests` 全量）
- [ ] **Step 5: 提交**

### Stream B（脊柱 / 宿主，可与 A 并行）

#### Task 5: `SessionTitleService` 改按"是否 Child"判定

**Files:**
- Modify: `src/spine/Seeing.Agent.Core/Services/SessionTitleService.cs:82-109,166-175`
- Test: `tests/spine/Seeing.Agent.Tests/Services/SessionTitleServiceTests.cs`

- [ ] **Step 1: 写失败测试**：主线后继（`Kind==Root`、`ParentSessionId` 非空）应满足 `ShouldEnsure`：

```csharp
[Fact]
public void ShouldEnsure_HandoffSuccessorWithParent_ShouldBeTrue()
{
    SessionTitleService.ShouldEnsureForTest(
        enabled: true, kind: SessionKind.Root, isChild: false,
        title: "新会话", realUserCount: 1, userMessage: "hi").Should().BeTrue();
}
```

- [ ] **Step 2: 运行确认失败**
- [ ] **Step 3: 实现**：`ShouldEnsure` 第 3 参数由 `string? parentId` 改为 `bool isChild`，删除 `if (!string.IsNullOrEmpty(parentId)) return false;`，改为 `if (isChild) return false;`；调用处置：

```csharp
var group = await _groupManager.GetGroupForSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
var isChild = group?.Members.FirstOrDefault(m =>
        string.Equals(m.SessionId, sessionId, StringComparison.Ordinal))
    ?.Relation == SessionRelation.Child;

if (!ShouldEnsure(options.Enabled, session.Kind, isChild, session.Title, realUserCount, userMessage))
    return null;
```
（同步更新任何直接测试 `ShouldEnsure` 的调用点。）

- [ ] **Step 4: 运行确认通过** (`FullyQualifiedName~SessionTitleServiceTests`)
- [ ] **Step 5: 提交**

#### Task 6: 删除会话时取消其**自身**在途执行

**Files:**
- Modify: `src/hosting/Seeing.Agent.Hosting/ChatOrchestrator.cs:180-199`
- Test: `tests/spine/Seeing.Agent.Tests/App/ChatOrchestratorCreateSessionTests.cs`（或新增 `ChatOrchestratorDeleteTests.cs`）

- [ ] **Step 1: 写失败测试**（Mock `ExecutionJobService.CancelBySessionAsync`）

```csharp
[Fact]
public async Task DeleteSessionAsync_ShouldCancelSelfExecution()
{
    // Arrange：mock executionJobService；delete(child)校验 CancelBySessionAsync(child)
    // 新增断言：CancelBySessionAsync(target) 亦被调用一次
}
```

- [ ] **Step 2: 运行确认失败**
- [ ] **Step 3: 实现**（在枚举子会话取消前，先取消自身）

```csharp
try { await _executionJobService.CancelBySessionAsync(sessionId, cancellationToken); }
catch (Exception ex) { _logger.LogWarning(ex, "删除会话时取消自身执行失败: {SessionId}", sessionId); }

var children = await _groupManager.ListChildrenAsync(sessionId, cancellationToken);
foreach (var child in children) { /* 现有取消逻辑 */ }
await _groupManager.RemoveSessionAsync(sessionId, cancellationToken);
```

- [ ] **Step 4: 运行确认通过**
- [ ] **Step 5: 提交**

#### Task 7: `session_handoff` 源必须为当前锚点

**Files:**
- Modify: `src/capabilities/Seeing.Agent.Tools.Session/SessionHandoffTool.cs:101-116`
- Test: `tests/capabilities/Seeing.Agent.Tools.Session.Tests/SessionHandoffToolTests.cs`

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public async Task Handoff_WhenSourceIsNotAnchor_ShouldFail()
{
    // Arrange：组内 root 已是 Predecessor（已有后继）
    // Act：对 root 再次 handoff
    // Assert：ToolResult 失败，且不创建新会话
}
```

- [ ] **Step 2: 运行确认失败**
- [ ] **Step 3: 实现**（组管理器已强制校验，工具侧提前给出友好失败）

```csharp
var member = group?.Members.FirstOrDefault(m => string.Equals(m.SessionId, source.Id, StringComparison.Ordinal));
if (member is { IsAnchor: false })
    return Failure("仅当前主线会话（锚点）可发起交接；请回到最新会话后再试。");
```

- [ ] **Step 4: 运行确认通过** (`FullyQualifiedName~SessionHandoffToolTests`)
- [ ] **Step 5: 提交**

### Stream C（WebUI 纯映射，可与 A/B 并行）

#### Task 8: `SessionRelationView` 关系/状态映射（纯函数）

**Files:**
- Create: `samples/Seeing.Agent.WebUI/Services/SessionRelationView.cs`
- Test: `tests/apps/Seeing.Agent.WebUI.Tests/Services/SessionRelationViewTests.cs`

- [ ] **Step 1: 写失败测试**（覆盖徽标文案、颜色、分区、备份判定、退化锚点）

```csharp
[Theory]
[InlineData(SessionRelation.HandoffPredecessor, false, null, "已交接")]
[InlineData(SessionRelation.Fork, false, "trim-backup 2026-01-01", "备份")]
[InlineData(SessionRelation.Fork, false, null, "分叉")]
[InlineData(SessionRelation.Child, false, null, "子代理（只读）")]
public void Badge_ShouldMapRelation(SessionRelation r, bool isAnchor, string? label, string expected)
    => SessionRelationView.BadgeText(r, isAnchor, label).Should().Be(expected);

[Fact]
public void DegenerateAnchor_ChildAnchor_ShouldNotShowMainLine()
{
    SessionRelationView.BadgeText(SessionRelation.Child, isAnchor: true, label: null)
        .Should().Be("子代理（只读）·主线缺失");
    SessionRelationView.Section(SessionRelation.Child, isAnchor: true).Should().Be(SessionSection.Children);
}

[Fact]
public void Anchor_ShouldShowMainLine()
    => SessionRelationView.BadgeText(SessionRelation.HandoffSuccessor, true, null).Should().Be("主线");
```

- [ ] **Step 2: 运行确认失败**
- [ ] **Step 3: 实现**

```csharp
public enum SessionSection { MainLineHistory, Derived, Children }

public static class SessionRelationView
{
    public const string TrimBackupPrefix = "trim-backup";

    public static string BadgeText(SessionRelation relation, bool isAnchor, string? label)
    {
        if (isAnchor)
            return relation == SessionRelation.Child ? "子代理（只读）·主线缺失" : "主线";
        return relation switch
        {
            SessionRelation.HandoffPredecessor => "已交接",
            SessionRelation.HandoffSuccessor => "交接后继",
            SessionRelation.Fork => (label?.StartsWith(TrimBackupPrefix, StringComparison.Ordinal) ?? false) ? "备份" : "分叉",
            SessionRelation.Child => "子代理（只读）",
            _ => "会话"
        };
    }

    public static SessionSection Section(SessionRelation relation, bool isAnchor) => relation switch
    {
        SessionRelation.Fork => SessionSection.Derived,
        SessionRelation.Child => SessionSection.Children,
        _ => isAnchor ? SessionSection.MainLineHistory : SessionSection.MainLineHistory
    };
}
```

- [ ] **Step 4: 运行确认通过** (`FullyQualifiedName~SessionRelationViewTests`)
- [ ] **Step 5: 提交**

> **Gate 1（必审）**：Tasks 2–4 完成后由审查子 Agent 核验归一/删除/交接与不变量一致性，通过后进入 Wave 2。

---

## Wave 2 — WebUI 组件（并行）

#### Task 9: `SessionWindowRegistry` 投影扩展与分区

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Services/SessionWindowRegistry.cs:21-31,220-235`
- Test: `tests/apps/Seeing.Agent.WebUI.Tests/Services/SessionWindowRegistryTests.cs`

- [ ] **Step 1: 写失败测试**：`WindowNode` 含 `IsAnchor`/`Label`；按 `SessionRelationView.Section` 分区排序（主线历史→派生→子会话；执行中由页面侧置顶，不在注册表）。
- [ ] **Step 2: 运行确认失败**
- [ ] **Step 3: 实现**

```csharp
public sealed record WindowNode(
    string SessionId,
    SessionRelation Relation,
    SessionKind Kind,
    string Title,
    bool IsActive,
    bool IsExecuting,
    bool IsAnchor,
    string? Label);
```
投影处（`:230`）补 `member.IsAnchor, member.Label`。

- [ ] **Step 4: 运行确认通过**
- [ ] **Step 5: 提交**

#### Task 10: `SessionWindow` Summary 状态分层 + 关系徽标 + 来源行 + 执行态回调

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Components/SessionWindow.razor`（Summary 块 `:169-189`；`GetLatestAssistantPreview` `:1471`）
- Modify：`SessionWindowContext.cs`（暴露 `Relation`、`IsAnchor`）
- Test: `tests/apps/Seeing.Agent.WebUI.Tests/Services/SessionWindowStatusTests.cs`（映射逻辑）

- [ ] **Step 1: 写失败测试**：状态分层判定（执行中/排队/失败/取消/完成/只读子代理）与"已完成保留 12~20 字预览摘要"。
- [ ] **Step 2: 运行确认失败**
- [ ] **Step 3: 实现**
  - 预览保留三分支不变；执行中额外渲染"动作行 + 已运行时长"；**删除进度条**。
  - 已完成：单行紧凑（标题 + 关系徽标 + 12~20 字预览 + 时间）。
  - 来源行：`父会话：X` → `SessionRelationView` 依据 relation 输出 `来源：X` / `已交接 → X` / `来源已删除`。
  - `[Parameter] public EventCallback<(string SessionId, bool IsExecuting)> OnExecutionStateChanged`；在状态变化处回调。
- [ ] **Step 4: 运行确认通过**
- [ ] **Step 5: 提交**

#### Task 11: `SessionToolbar` 徽标按 relation

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Components/SessionToolbar.razor:57-85`
- Test: 复用 Task 8 映射测试；组件级测试可选。

- [ ] **Step 1–2: 写失败测试并确认失败**（`ShowSubAgentBadge` → 由 `Relation==Child` 驱动；主线/已交接/分叉徽标由 `SessionRelationView` 输出）
- [ ] **Step 3: 实现**：`@if (Context.Relation != SessionRelation.None) { <Badge Text="@SessionRelationView.BadgeText(...)" /> }`。
- [ ] **Step 4: 运行确认通过**
- [ ] **Step 5: 提交**

#### Task 12: `SessionRelationBar` 顶部关系条

**Files:**
- Create: `samples/Seeing.Agent.WebUI/Components/SessionRelationBar.razor`
- Test: `tests/apps/Seeing.Agent.WebUI.Tests/Components/SessionRelationBarTests.cs`

- [ ] **Step 1: 写失败测试**：只渲染主线链（前任→锚点）；>3 节点折叠为 `… N 个更早`；分支计数 chip `⑂n · 👤n ▾`；点击节点触发 `OnSelect(sessionId)`；「关系 ▸」触发 `OnOpenDrawer`。
- [ ] **Step 2: 运行确认失败**
- [ ] **Step 3: 实现**：输入 `SessionGroup group`（组快照）；按 `ParentSessionId` 反向 + 主线关系筛选重建链（排除 Fork/Child/None）；锚点高亮含"执行中"脉冲（由参数传入执行态）。
- [ ] **Step 4: 运行确认通过**
- [ ] **Step 5: 提交**

#### Task 13: `SessionRelationDrawer` 关系抽屉

**Files:**
- Create: `samples/Seeing.Agent.WebUI/Components/SessionRelationDrawer.razor`
- Test: `tests/apps/Seeing.Agent.WebUI.Tests/Components/SessionRelationDrawerTests.cs`

- [ ] **Step 1: 写失败测试**：树结构（主线链 + 各节点下挂分叉/子代理）；节点操作回调（`OnSelect`/`OnRename`/`OnOpenNewTab`/`OnDelete`）；删除前提示"先取消执行"。
- [ ] **Step 2: 运行确认失败**
- [ ] **Step 3: 实现**：输入 `SessionGroup group` + `Open` + 回调；节点徽标用 `SessionRelationView`。
- [ ] **Step 4: 运行确认通过**
- [ ] **Step 5: 提交**

> **Gate 2（必审）**：Tasks 9–13 完成后由审查子 Agent 核验三处职责不重复、状态分层、订阅去重（复用 `WindowsChanged`）。

---

## Wave 3 — 接线与文档

#### Task 14: 页面接线（顶条 + 抽屉 + 侧栏分区 + 交接导航 + 执行态收集）

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Pages/Session.razor:55-160,286-310`
- Modify: `samples/Seeing.Agent.WebUI/Pages/Conference.razor:30-65,264-271`
- Test: 现有 `SessionWindowContextCapabilityTests`/页面集成测试按需更新

- [ ] **Step 1: 写失败测试**：分区渲染顺序；交接成功 → `NavigateTo("/session/{target_session_id}")`；点击切换不改变 URL。
- [ ] **Step 2: 运行确认失败**
- [ ] **Step 3: 实现**
  - 顶条/抽屉数据源：`ISessionGroupManager.GetGroupForSessionAsync(anchorOrCurrent)`，订阅 `SessionWindowRegistry.WindowsChanged`（复用 `Session.razor:292-293`），不新增组事件订阅。
  - 侧栏：`SideWindows` 用 `SessionRelationView.Section` 分区；页面侧收集 `OnExecutionStateChanged` 得到 `Dictionary<sessionId,bool>` 用于"执行中置顶"。
  - 交接导航：读取 `toolCall.Metadata["target_session_id"]`（`EventStreamHandler.cs:487-490` 已流入）成功时导航；兜底仅限交接驱动。
- [ ] **Step 4: 运行确认通过**（`FullyQualifiedName~SessionWindow`）
- [ ] **Step 5: 提交**

#### Task 15: 文档与注释同步

**Files:**
- Modify: `docs/architecture/09-session-tools-and-groups-release-notes.md`
- Modify: `src/primitives/Seeing.Session/Management/SessionGroupManager.cs:705-708`（XML）
- Modify: `docs/superpowers/specs/2026-09-18-session-tools-and-session-groups-design.md`（旧"锚点必须 None"）

- [ ] **Step 1: 更新文档**：新增 `HandoffPredecessor` 语义、锚点归一规则、锚点后移与导航、旧组不迁移、执行态边界不变。
- [ ] **Step 2: 提交**

> **Gate 3（轻审）**：Tasks 14–15 + 全量回归。

---

## Final Verification

- [ ] `dotnet build Seeing.Agent.slnx` → 0 error
- [ ] 全量测试（VSTest，逐个 DLL）：
  - `tests/primitives/Seeing.Session.Tests`
  - `tests/spine/Seeing.Agent.Tests`
  - `tests/spine/Seeing.Agent.Invariants.Tests`
  - `tests/capabilities/Seeing.Agent.Tools.Session.Tests`
  - `tests/apps/Seeing.Agent.WebUI.Tests`
  - `tests/capabilities/Seeing.Agent.Acp.Tests`
  - `tests/capabilities/Seeing.Agent.Scheduler.Tests`
- [ ] 手工 E2E（Playwright + Edge，`http://localhost:53066`）：交接→自动导航后继并锚点后移；顶条主线链+计数；抽屉树；侧栏分区（执行中大卡无进度条、已完成紧凑）；点击切换 URL 不变。

---

## Self-Review

- **Spec coverage**：§2 模型 → T1–T3；§3 生命周期 → T3/T4/T5/T6/T7/T14；§4 展示 → T8–T14；§5 影响面 → 全部任务；§6 兼容 → T4/T9/T14（旧数据以 `IsAnchor` 定主线）；§7 测试 → 各任务 Step 1。
- **Placeholder scan**：无 TODO/TBD；UI 任务给出映射函数与测试用例，组件按 `SessionRelationView` 唯一映射实现。
- **Type consistency**：`SessionRelation.HandoffPredecessor`、`NormalizeAnchor(group, hint)`、`WindowNode(..., IsAnchor, Label)`、`SessionRelationView.BadgeText/Section`、`SessionSection` 在全部任务中一致引用。
