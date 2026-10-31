# 已归档会话删除与批量删除 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让「会话管理」页的已归档视图支持单项删除与批量删除，并保证删除归档会话时磁盘/内存中的归档副本被真正清理。

**Architecture:** 先在存储层把 `DeleteAsync` 做成「彻底删除」（同时清活跃数据与归档副本），再在 `SessionsPage.razor` 复用普通视图已有的勾选/批量删除机制，把作用列表按当前视图切换。

**Tech Stack:** .NET 10 / C# / xUnit + FluentAssertions / Blazor + Ant Design Blazor

**Spec:** `docs/superpowers/specs/2026-10-31-archived-session-delete-design.md`

## Global Constraints

- 注释与界面文案一律中文。
- 依赖方向不变：`src/primitives/Seeing.Session` 不得引用上层包。
- 不新增公开接口/端口；删除语义 = 彻底删除（含归档副本）。
- 测试命名：`{方法}_{场景}_Should{预期结果}`。
- 存储层删除失败只记日志、不抛异常（沿用 `DeleteRefDirectory` 策略）。
- 批量恢复明确不做。

## Review Focus

每条都在拥有它的任务里有对应验证：

- 删除**未归档**会话时归档副本不存在：不得抛异常，也不得因归档清理而跳过活跃文件删除 → Task 1 `DeleteAsync_ActiveSessionWithoutArchive_ShouldStillDelete`
- 归档文件被占用/删除失败：删除流程仍应完成，仅记警告 → Task 1 `DeleteAsync_ArchivedFileLocked_ShouldStillDeleteActiveData`
- 归档视图批量删除后：列表、统计数、勾选状态都不残留 → Task 3 手工清单第 3 条
- 视图切换的勾选污染：归档视图勾选后返回普通视图，旧勾选必须已被清空（既有 `RefreshSessionsAsync` → `ClearSelection` 行为，本次要确保全选逻辑不会跨列表误选）→ Task 3 手工清单第 4 条
- 删除归档组中的某个成员：组内其余归档成员仍可恢复，组记录不被误删 → Task 2 `RemoveSessionAsync_ArchivedGroupMember_ShouldKeepOtherMembersArchived`

---

### Task 1: `FileSessionStore.DeleteAsync` 清理归档副本

**Files:**
- Modify: `src/primitives/Seeing.Session/Storage/FileSessionStore.cs:277-304`（`DeleteAsync`）并新增私有方法 `DeleteArchivedFile`
- Test: `tests/primitives/Seeing.Session.Tests/Storage/SessionSummaryAndArchiveTests.cs`

**Interfaces:**
- Consumes: `GetArchiveDirectory()`（已有，返回 `{base}/_archive`）、`AtomicFileWrite.TryDelete(string)`（已有）
- Produces: `FileSessionStore.DeleteAsync(string sessionId, CancellationToken ct)` 行为扩展（签名不变），供上层 `SessionManager.DeleteAsync` 直接复用

- [ ] **Step 1: 写失败测试**

在 `SessionSummaryAndArchiveTests.cs` 的 `ArchiveAsync_ShouldReturnFalse_WhenSessionMissing` 之后新增：

```csharp
[Fact]
public async Task DeleteAsync_AfterArchive_ShouldRemoveArchivedCopyAndSummary()
{
    var dir = NewTempDir();
    try
    {
        var store = new FileSessionStore(dir);
        var session = await SeedAsync(store);
        await store.ArchiveAsync(session.Id);
        var archivePath = Path.Combine(dir, "_archive", $"{session.Id}.json.gz");
        File.Exists(archivePath).Should().BeTrue();

        await store.DeleteAsync(session.Id);

        File.Exists(archivePath).Should().BeFalse();
        var archived = new List<SessionSummary>();
        await foreach (var s in store.ListArchivedAsync())
            archived.Add(s);
        archived.Should().BeEmpty();
    }
    finally
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}
```

再追加回归用例（保证归档清理不破坏「删除活跃会话」的既有语义）：

```csharp
[Fact]
public async Task DeleteAsync_ActiveSessionWithoutArchive_ShouldStillDelete()
{
    var dir = NewTempDir();
    try
    {
        var store = new FileSessionStore(dir);
        var session = await SeedAsync(store);

        await store.DeleteAsync(session.Id);

        File.Exists(Path.Combine(dir, $"{session.Id}.json")).Should().BeFalse();
        (await store.LoadAsync(session.Id)).Should().BeNull();
    }
    finally
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}
```

再加一条「归档文件被占用」的守护用例（确保删除流程不因归档清理失败而中断）：

```csharp
[Fact]
public async Task DeleteAsync_ArchivedFileLocked_ShouldStillDeleteActiveData()
{
    var dir = NewTempDir();
    try
    {
        var store = new FileSessionStore(dir);
        var session = await SeedAsync(store);
        await store.ArchiveAsync(session.Id);
        var archivePath = Path.Combine(dir, "_archive", $"{session.Id}.json.gz");

        using (new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var act = async () => await store.DeleteAsync(session.Id);
            await act.Should().NotThrowAsync();
        }

        (await store.LoadAsync(session.Id)).Should().BeNull();
    }
    finally
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/primitives/Seeing.Session.Tests --filter FullyQualifiedName~DeleteAsync_`
Expected: `DeleteAsync_AfterArchive_ShouldRemoveArchivedCopyAndSummary` FAIL（`File.Exists(archivePath)` 仍为 true）；另两条 PASS（回归基线）

- [ ] **Step 3: 实现归档副本删除**

`DeleteAsync` 在 `DeleteRefDirectory(sessionId);` 之后、取 `filePath` 之前调用 `DeleteArchivedFile(sessionId);`；活跃文件不存在时仍保留原有的 debug 日志与 `return`。

新增私有方法（放在 `DeleteRefDirectory` 之后）：

```csharp
/// <summary>删除归档副本（_archive/{sessionId}.json.gz）；不存在或失败均不影响主删除流程。</summary>
private void DeleteArchivedFile(string sessionId)
{
    var archivePath = Path.Combine(GetArchiveDirectory(), $"{sessionId}.json.gz");
    if (!File.Exists(archivePath))
        return;

    AtomicFileWrite.TryDelete(archivePath);
    if (File.Exists(archivePath))
        _logger?.LogWarning("删除归档副本失败: {SessionId}, {Path}", sessionId, archivePath);
    else
        _logger?.LogInformation("删除归档副本: {SessionId}", sessionId);
}
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test tests/primitives/Seeing.Session.Tests --filter FullyQualifiedName~SessionSummaryAndArchiveTests`
Expected: PASS（全部归档相关测试通过，含既有 Archive/Restore）

- [ ] **Step 5: 提交**

```bash
git add src/primitives/Seeing.Session/Storage/FileSessionStore.cs tests/primitives/Seeing.Session.Tests/Storage/SessionSummaryAndArchiveTests.cs
git commit -m "fix(session): 删除会话时清理归档副本"
```

---

### Task 2: `InMemorySessionStore.DeleteAsync` 清理归档表

**Files:**
- Modify: `src/primitives/Seeing.Session/Storage/InMemorySessionStore.cs:61-72`（`DeleteAsync`）
- Test: `tests/primitives/Seeing.Session.Tests/Storage/SessionSummaryAndArchiveTests.cs`
- Test: `tests/primitives/Seeing.Session.Tests/Management/SessionGroupArchiveTests.cs`

**Interfaces:**
- Consumes: `_archived`（已有 `ConcurrentDictionary<string, SessionData>`）、`SessionGroupTestHarness.SessionStore`（`InMemorySessionStore`）
- Produces: `InMemorySessionStore.DeleteAsync` 行为扩展（签名不变）

- [ ] **Step 1: 写失败测试**

在 Task 1 新增的测试之后追加：

```csharp
[Fact]
public async Task InMemoryStore_DeleteAsync_ShouldRemoveArchivedEntry()
{
    var store = new InMemorySessionStore();
    var session = SessionData.Create(selectedAgent: "plan");
    await store.SaveAsync(session);
    await store.ArchiveAsync(session.Id);

    await store.DeleteAsync(session.Id);

    var archived = new List<SessionSummary>();
    await foreach (var s in store.ListArchivedAsync())
        archived.Add(s);
    archived.Should().BeEmpty();
}
```

- [ ] **Step 2: 写组级回归测试**

在 `SessionGroupArchiveTests.cs` 末尾追加（验证删除归档组内一个成员不影响其余成员恢复）：

```csharp
[Fact]
public async Task RemoveSessionAsync_ArchivedGroupMember_ShouldKeepOtherMembersArchived()
{
    using var h = new SessionGroupTestHarness();
    var root = h.CreateRoot("g-root");
    await h.Manager.EnsureForSessionAsync(root.Id);
    var child = h.CreatePlainSession(SessionKind.SubAgent);
    child.Title = "child";
    await h.SessionStore.SaveAsync(child);
    var group = await h.Manager.GetGroupForSessionAsync(root.Id);
    await h.Manager.AddMemberAsync(group!.Id, new SessionGroupMember
    {
        SessionId = child.Id,
        Relation = SessionRelation.Child,
        ParentSessionId = root.Id
    });
    await h.Manager.ArchiveGroupAsync(group.Id);

    await h.Manager.RemoveSessionAsync(child.Id, TestContext.Current.CancellationToken);

    var archived = new List<SessionSummary>();
    await foreach (var s in h.SessionStore.ListArchivedAsync())
        archived.Add(s);
    archived.Should().ContainSingle(s => s.Id == root.Id);

    var remaining = await h.Manager.GetGroupAsync(group.Id);
    remaining.Should().NotBeNull();
    remaining!.Members.Should().ContainSingle(m => m.SessionId == root.Id);

    (await h.Manager.RestoreGroupAsync(group.Id)).Should().BeTrue();
    (await h.SessionStore.LoadAsync(root.Id)).Should().NotBeNull();
}
```

- [ ] **Step 3: 运行测试确认失败**

Run: `dotnet test tests/primitives/Seeing.Session.Tests --filter FullyQualifiedName~DeleteAsync_|FullyQualifiedName~RemoveSessionAsync_ArchivedGroupMember`
Expected: 两个新用例 FAIL（归档表/归档列表仍含被删成员）

- [ ] **Step 4: 实现**

`DeleteAsync` 中在 `_sessions.TryRemove(sessionId, out _);` 之后追加一行：

```csharp
_archived.TryRemove(sessionId, out _);
```

- [ ] **Step 5: 运行测试确认通过**

Run: `dotnet test tests/primitives/Seeing.Session.Tests --filter FullyQualifiedName~SessionSummaryAndArchiveTests`
Run: `dotnet test tests/primitives/Seeing.Session.Tests --filter FullyQualifiedName~SessionGroupArchiveTests`
Expected: 全部 PASS

- [ ] **Step 6: 提交**

```bash
git add src/primitives/Seeing.Session/Storage/InMemorySessionStore.cs tests/primitives/Seeing.Session.Tests/Storage/SessionSummaryAndArchiveTests.cs tests/primitives/Seeing.Session.Tests/Management/SessionGroupArchiveTests.cs
git commit -m "fix(session): 内存存储删除会话时清理归档表"
```

---

### Task 3: 归档视图的单项删除与批量删除（WebUI）

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Pages/SessionsPage.razor`（模板 80-226 行；代码块 558-632 行）

**Interfaces:**
- Consumes: `DeleteSessionsCoreAsync(IEnumerable<string>)`、`DeleteSession(string)`、`ToggleSelect(string, bool)`、`IsSelected(string)`、`ClearSelection()`（均已存在）
- Produces: 无新公开成员；新增私有属性 `CurrentSessions` 与私有方法 `GetBatchDeleteConfirmText()`

- [ ] **Step 1: 批量栏确认文案按视图区分**

把批量栏的：

```razor
<Popconfirm Title="@($"确定删除所选 {_selectedIds.Count} 个会话？将同步删除其子会话。")"
            OnConfirm="@DeleteSelectedAsync">
```

改为：

```razor
<Popconfirm Title="@GetBatchDeleteConfirmText()"
            OnConfirm="@DeleteSelectedAsync">
```

- [ ] **Step 2: 归档卡片加勾选与删除**

归档卡片（`_showArchived` 分支）的 `<div class="session-card-actions">` 替换为：

```razor
<div class="session-card-actions">
    <Checkbox Checked="@IsSelected(session.Id)"
              CheckedChanged="@((bool value) => ToggleSelect(session.Id, value))" />
    <div class="session-card-action-buttons">
        <Button Type="@ButtonType.Primary" Size="@ButtonSize.Small"
                Icon="@IconType.Outline.Rollback"
                OnClick="@(() => RestoreSession(session.Id))">
            恢复
        </Button>
        <Popconfirm Title="确定删除此已归档会话？该操作不可恢复，将同步删除其子会话。"
                    OnConfirm="@(() => DeleteSession(session.Id))">
            <Button Type="@ButtonType.Text" Size="@ButtonSize.Small"
                    Danger Icon="@IconType.Outline.Delete">
                删除
            </Button>
        </Popconfirm>
    </div>
</div>
```

同时把该卡片的 `<div Class="session-card-wrapper">` 改为：

```razor
<div Class="session-card-wrapper @(IsSelected(session.Id) ? "session-card-selected" : "")">
```

- [ ] **Step 3: 全选/批量作用列表按视图切换**

在 `@code` 的 `ClearSelection()` 之后新增：

```csharp
/// <summary>当前视图下的可选列表（归档视图 → 归档列表；普通视图 → 筛选列表）。</summary>
private IReadOnlyList<SessionSummary> CurrentSessions =>
    _showArchived ? _archivedSessions : _filteredSessions;

/// <summary>批量删除确认文案：归档删除不可恢复，单独提示。</summary>
string GetBatchDeleteConfirmText() => _showArchived
    ? $"确定删除所选 {_selectedIds.Count} 个已归档会话？该操作不可恢复，将同步删除其子会话。"
    : $"确定删除所选 {_selectedIds.Count} 个会话？将同步删除其子会话。";
```

把 `IsAllSelected()` 改为：

```csharp
bool IsAllSelected() =>
    CurrentSessions.Count > 0 && CurrentSessions.All(s => _selectedIds.Contains(s.Id));
```

把 `OnToggleSelectAll(bool value)` 内的两处 `_filteredSessions` 改为 `CurrentSessions`。

- [ ] **Step 4: 构建验证**

Run: `dotnet build samples/Seeing.Agent.WebUI`
Expected: 构建成功，无警告升级为错误

- [ ] **Step 5: 手工验证清单**

`dotnet run --project samples/Seeing.Agent.WebUI` 后：

1. 普通视图：勾选/全选/批量删除、单项删除行为与改动前一致；
2. 进入「已归档」：卡片出现勾选框与「删除」；单项删除确认后卡片消失，`_archive` 下文件消失；
3. 归档视图勾选 2 个 → 批量栏显示「已选 2 项」→ 批量删除 → 列表为空且统计不反弹；
4. 归档视图勾选后点「返回会话」：普通视图无残留勾选；
5. 归档组中只删一个成员：其余成员仍在归档列表且可恢复。

- [ ] **Step 6: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Pages/SessionsPage.razor
git commit -m "feat(webui): 已归档会话支持删除与批量删除"
```

---

## 验证

- `dotnet test tests/primitives/Seeing.Session.Tests --filter FullyQualifiedName~SessionSummaryAndArchiveTests`
- `dotnet build samples/Seeing.Agent.WebUI`
- Task 3 Step 5 手工清单
