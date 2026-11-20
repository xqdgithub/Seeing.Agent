# 多根文件树 + 协同目录归位到会话 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 协同目录的增删回到会话工具栏，文件工作台把项目根与协同目录并列成一棵树。

**Architecture:** 会话层（`Seeing.Agent.WebUI`）持有 `ICollaborationDirectories` 写端口并渲染管理弹窗；文件工作台（`Seeing.Agent.WebUI.Files`）只消费 `IFileWorkspaceFactory.ListAsync` 的只读根列表，用 `FileWorkspaceSet` 按 `RootId` 路由所有读写。树状态从「相对路径」升级为「`FileResourceId`」，跨根同名路径互不影响。

**Tech Stack:** .NET 10 / Blazor / AntDesign / xUnit + FluentAssertions + Moq

**Spec:** `docs/superpowers/specs/2026-11-20-multi-root-file-tree-design.md`

## Global Constraints

- 文件工作台程序集不得引用 `ICollaborationDirectories`（写端口只属于会话层）。
- 所有路径边界判定只用 `PathBoundary`，禁止内联前缀比较。
- 测试禁止固定时长等待；用条件轮询或完成信号。
- 测试运行一律 `dotnet vstest <dll>`（MTP 无法发现测试）。
- 构建/测试必须串行（`-m:1`，逐个 vstest）。

## Review Focus

- 两个根下存在同名文件/目录时，树展开、选中高亮、磁盘变更刷新不能串到另一个根。
- 根目录被删除后再次打开：条目仍保留并显示「不可用」，不能静默消失或抛异常。
- 冷会话（进程重启后直接打开旧会话）必须显示协同目录，不能退化成只有项目根。
- 协同目录被移除后，已打开的该根标签不能继续静默写入；应在下一次操作明确失败。
- 相对路径进入文件工作台时按所属根的 `RootPath` 解析，不能落到进程 CWD。

---

### Task 1: `FileWorkspaceSet` 多根解析

**Files:**
- Create: `samples/Seeing.Agent.WebUI.Files/Services/FileWorkspaceSet.cs`
- Test: `tests/apps/Seeing.Agent.WebUI.Files.Tests/FileWorkspaceSetTests.cs`

**Interfaces:**
- Consumes: `IFileWorkspace`（`RootId`、`RootPath`）、`PathBoundary.IsWithin`
- Produces:
  ```csharp
  public sealed class FileWorkspaceSet
  {
      public FileWorkspaceSet(IReadOnlyList<IFileWorkspace> roots);
      public IReadOnlyList<IFileWorkspace> Roots { get; }
      public IFileWorkspace? Find(FileResourceId id);
      public IFileWorkspace? FindByPath(string absolutePath);
  }
  ```

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void Find_ForeignRootId_ShouldReturnNull();
// Arrange: 两个 mock IFileWorkspace，RootId 分别为 "r1"/"r2"、RootPath 为两个临时目录
// Assert: set.Find(new FileResourceId("r3", "a.txt")).Should().BeNull()

[Fact]
public void FindByPath_LongestPrefixWins();
// Arrange: root1 = D:\a, root2 = D:\ab（用 Path.Combine(临时目录, "a") / "ab"）
// Assert: set.FindByPath(Path.Combine(root2, "x.txt"))!.RootId.Should().Be("r2")
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet vstest tests/apps/Seeing.Agent.WebUI.Files.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Files.Tests.dll --TestCaseFilter:"FullyQualifiedName~FileWorkspaceSetTests"`
Expected: 编译失败（`FileWorkspaceSet` 不存在）

- [ ] **Step 3: 实现 `FileWorkspaceSet`**

`Find` 按 `RootId` 精确匹配；`FindByPath` 用 `PathBoundary.IsWithin` 过滤后按 `RootPath.Length` 降序取第一条。空集合返回 null。

- [ ] **Step 4: 运行测试确认通过**

Run: 同上
Expected: PASS（2 个用例）

- [ ] **Step 5: 提交**

```bash
git add samples/Seeing.Agent.WebUI.Files/Services/FileWorkspaceSet.cs tests/apps/Seeing.Agent.WebUI.Files.Tests/FileWorkspaceSetTests.cs
git commit -m "feat(files): add multi-root workspace set"
```

---

### Task 2: 树状态改按 `FileResourceId` 关联

**Files:**
- Modify: `samples/Seeing.Agent.WebUI.Files/Services/FileTreeState.cs`
- Modify: `samples/Seeing.Agent.WebUI.Files/Components/FileTreeNode.razor`
- Modify: `samples/Seeing.Agent.WebUI.Files/Components/FileTree.razor`
- Modify: `samples/Seeing.Agent.WebUI.Files/Components/FilePanelHost.razor`
- Test: `tests/apps/Seeing.Agent.WebUI.Files.Tests/FileTreeStateTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public FileResourceId? SelectedId { get; private set; }
  public void SetSelected(FileResourceId? id);
  public static bool IsDirectChildOf(FileResourceId changed, FileResourceId dir);
  ```
- `FileTreeNode` 新增 `[Parameter] public string? Badge { get; set; }`（本任务先留空，Task 3 使用）。

- [ ] **Step 1: 改测试（新语义）**

把 4 个 `SetSelected(string)` 用例改为 `SetSelected(new FileResourceId("r1", "..."))`，断言 `SelectedId`；新增：

```csharp
[Fact]
public void IsDirectChildOf_SameRelativePathDifferentRoot_ShouldNotMatch()
{
    var a = new FileResourceId("r1", "src/a.txt");
    var b = new FileResourceId("r2", "src/a.txt");
    FileTreeState.IsDirectChildOf(a, new FileResourceId("r1", "src")).Should().BeTrue();
    FileTreeState.IsDirectChildOf(b, new FileResourceId("r1", "src")).Should().BeFalse();
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet vstest tests/apps/Seeing.Agent.WebUI.Files.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Files.Tests.dll --TestCaseFilter:"FullyQualifiedName~FileTreeStateTests"`
Expected: 编译失败或断言失败

- [ ] **Step 3: 实现状态与调用点**

- `FileTreeState`：`SelectedId` 存 `FileResourceId?`；比较用 `RootId` + 忽略大小写的相对路径（去掉 `\`→`/`）。
- `FileTreeNode`：`IsSelected` 比较 `Entry.Id` 与 `TreeState.SelectedId`；`SelectionInDir` 用 `IsDirectChildOf(SelectedId, Entry.Id)` 的祖先前缀判定；`OnTreeChanged` 改为 `IsDirectChildOf(evt.Id, Entry.Id)`。
- `FileTree`：根目录判定传 `new FileResourceId(Workspace.RootId, "")`。
- `FilePanelHost.SyncTreeSelection`：`TreeState.SetSelected(active?.Id)`。

- [ ] **Step 4: 运行确认通过**

Run: 同上 + `dotnet build Seeing.Agent.slnx -v q --nologo -m:1`
Expected: 测试 PASS、构建 0 error

- [ ] **Step 5: 提交**

```bash
git add samples/Seeing.Agent.WebUI.Files tests/apps/Seeing.Agent.WebUI.Files.Tests/FileTreeStateTests.cs
git commit -m "refactor(files): key tree state by FileResourceId"
```

---

### Task 3: 文件树并列多根

**Files:**
- Create: `samples/Seeing.Agent.WebUI.Files/Services/FileTreeRoots.cs`
- Modify: `samples/Seeing.Agent.WebUI.Files/Components/FileTree.razor`
- Modify: `samples/Seeing.Agent.WebUI.Files/Components/FileTreeNode.razor`
- Modify: `samples/Seeing.Agent.WebUI.Files/wwwroot/files-panel.css`
- Test: `tests/apps/Seeing.Agent.WebUI.Files.Tests/FileTreeRootsTests.cs`

**Interfaces:**
- Consumes: `FileWorkspaceSet`（Task 1）
- Produces:
  ```csharp
  public static class FileTreeRoots
  {
      public static FileEntry Entry(IFileWorkspace workspace);
      public static string Badge(FileWorkspaceRootKind kind);   // "项目" / "协同"
      public static string DisplayName(FileWorkspaceRoot root); // Label，不存在时追加「（不可用）」
  }
  ```
- `FileTree` 参数变更为 `[Parameter, EditorRequired] public IReadOnlyList<IFileWorkspace> Roots { get; set; }`。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void Badge_Project_ShouldBeProject();
[Fact]
public void Badge_Collaboration_ShouldBeCollab();
[Fact]
public void DisplayName_MissingRoot_ShouldAppendUnavailable();
// root.Exists == false → 结果包含「不可用」
[Fact]
public void Entry_ShouldUseRootIdAndEmptyRelativePath();
// Entry(ws).Id == new FileResourceId(ws.RootId, "")，IsDirectory == true
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet vstest tests/apps/Seeing.Agent.WebUI.Files.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Files.Tests.dll --TestCaseFilter:"FullyQualifiedName~FileTreeRootsTests"`
Expected: 编译失败

- [ ] **Step 3: 实现 helper 与树**

- `FileTree.razor` 遍历 `Roots`：
  ```razor
  @foreach (var root in Roots)
  {
      <FileTreeNode Entry="FileTreeRoots.Entry(root)" Workspace="root" OnOpen="OnOpen"
                    Depth="0" Badge="FileTreeRoots.Badge(root.Root.Kind)" />
  }
  ```
- `FileTreeNode` 目录行渲染 `Badge`（`.file-tree-badge`）+ 悬浮 `title="@Entry.Id"` 显示完整路径。
- CSS：`.file-tree-badge--project`（蓝）、`.file-tree-badge--collab`（橙）、失效根红字。
- 删除 `FileTree` 的 `_roots.Count == 0` 判断，改为 `Roots.Count == 0`。

- [ ] **Step 4: 运行确认通过**

Run: 同上 + 构建
Expected: PASS、0 error

- [ ] **Step 5: 提交**

```bash
git add samples/Seeing.Agent.WebUI.Files tests/apps/Seeing.Agent.WebUI.Files.Tests/FileTreeRootsTests.cs
git commit -m "feat(files): render all roots as top-level tree nodes"
```

---

### Task 4: `FileEditor` 按标签 id 解析根

**Files:**
- Modify: `samples/Seeing.Agent.WebUI.Files/Components/FileEditor.razor`

**Interfaces:**
- Consumes: `FileWorkspaceSet.Find(FileResourceId)`
- 参数变更：删除 `IFileWorkspace Workspace`，新增 `[Parameter, EditorRequired] public FileWorkspaceSet Workspaces { get; set; }`。

- [ ] **Step 1: 替换所有读写调用**

6 处 `Workspace.Xxx(Workspace.GetAbsolutePath(f.Id))` 改为：

```csharp
var ws = Workspaces.Find(f.Id);
if (ws is null) { ReportSave("文件所属目录已不在本会话中", error: true); return; }
result = await ws.WriteAsync(ws.GetAbsolutePath(f.Id), ...);
```

`TryGetConflictDiffAsync` / `ReloadFromDiskAsync` 同样处理，找不到时返回 null 或提示。

- [ ] **Step 2: 构建确认**

Run: `dotnet build Seeing.Agent.slnx -v q --nologo -m:1`
Expected: 0 error（`FilePanelHost` 的调用点在 Task 5 一起改；若先编译失败，只改 `FilePanelHost` 的传参一行）

- [ ] **Step 3: 提交**

```bash
git add samples/Seeing.Agent.WebUI.Files/Components/FileEditor.razor
git commit -m "refactor(files): resolve editor workspace by tab id"
```

---

### Task 5: `FilePanelHost` 多根接线、移除管理 UI

**Files:**
- Modify: `samples/Seeing.Agent.WebUI.Files/Components/FilePanelHost.razor`
- Modify: `samples/Seeing.Agent.WebUI.Files/wwwroot/files-panel.css`

- [ ] **Step 1: 删除管理 UI 与依赖**

删除 `@inject ICollaborationDirectories`、根工具条（下拉/`+`/移除/输入行）、`_showAddRoot`、`_newRootPath`、`SwitchRootAsync`、`AddRootAsync`、`RemoveActiveRootAsync`、`ReloadRootsAsync`、`_activeRootId`。

- [ ] **Step 2: 改为多根 + 集合**

- `_workspaces = new FileWorkspaceSet(await WorkspaceFactory.ListAsync(SessionId))`
- `_roots = _workspaces.Roots`
- `TryReadAsync(id)`：`_workspaces.Find(id)` 为空时返回"文件所属目录已不在本会话中"。
- `OpenPathAsync` / `EnsureActiveAsync`：用 `await WorkspaceFactory.ResolveAsync(SessionId, path)` 路由，null 时 `SetError("路径不属于本会话的任何根")`。
- `WireCoordinator`：为每个根创建一个 `FileViewerCoordinator`，事件统一 `TreeState.Notify` + 该根的 `ApplyAsync`；`Dispose` 释放全部。
- `<FileTree Roots="_roots" OnOpen="OnOpenFileAsync" />`、`<FileEditor Tabs="_tabs" Workspaces="_workspaces" @ref="_editor" />`。

- [ ] **Step 3: 构建 + 现有测试**

Run:
```
dotnet build Seeing.Agent.slnx -v q --nologo -m:1
dotnet vstest tests/apps/Seeing.Agent.WebUI.Files.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Files.Tests.dll
dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll
```
Expected: 0 error；两套测试全绿

- [ ] **Step 4: 提交**

```bash
git add samples/Seeing.Agent.WebUI.Files
git commit -m "feat(files): wire multi-root panel without directory management"
```

---

### Task 6: 会话工具栏入口 + 协同目录弹窗

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Components/SessionToolbar.razor`
- Create: `samples/Seeing.Agent.WebUI/Components/CollaborationDirectoriesModal.razor`
- Test: `tests/apps/Seeing.Agent.WebUI.Tests/Components/SessionToolbarCollaborationContractTests.cs`

**Interfaces:**
- Consumes: `ICollaborationDirectories`（`GetAsync` / `AddAsync` / `RemoveAsync`）、`IWorkspaceProvider.GetProjectRoot()`
- 弹窗参数：`[Parameter] public bool Visible`、`[Parameter] public string? SessionId`、`[Parameter] public EventCallback OnClose`。

- [ ] **Step 1: 写契约测试**

```csharp
[Fact]
public void Toolbar_ShouldHostCollaborationDirectoryEntry()
{
    var razor = ReadWebUiFile("Components", "SessionToolbar.razor");
    razor.Should().Contain("ICollaborationDirectories");
    razor.Should().Contain("_collaborationVisible");
    razor.Should().Contain("CollaborationDirectoriesModal");
}

[Fact]
public void Modal_ShouldUseWritePortAndShowPaths()
{
    var razor = ReadWebUiFile("Components", "CollaborationDirectoriesModal.razor");
    razor.Should().Contain("AddAsync");
    razor.Should().Contain("RemoveAsync");
    razor.Should().Contain("不可用");
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll --TestCaseFilter:"FullyQualifiedName~SessionToolbarCollaborationContractTests"`
Expected: FAIL（文件不存在）

- [ ] **Step 3: 实现工具栏按钮与弹窗**

- `SessionToolbar`：注入 `ICollaborationDirectories`；`Context.SessionId` 非空时在右侧工具栏渲染按钮（链接图标 + 「协同目录」），`OnClick` 置 `_collaborationVisible = true`；页面底部渲染 `<CollaborationDirectoriesModal Visible="_collaborationVisible" SessionId="@Context.SessionId" OnClose="..."/>`。
- 弹窗：
  - `OnParametersSetAsync`：`Visible && SessionId` 变化时 `LoadAsync()`。
  - 列表第一行项目根（`IWorkspaceProvider.GetProjectRoot()`，只读）。
  - 协同目录行显示名称/完整路径/「不可用」，右侧「移除」调 `RemoveAsync`。
  - 底部输入绝对路径 + 「添加」调 `AddAsync`。
  - 成功/失败用 `MessageService`；写后重新 `GetAsync`。

- [ ] **Step 4: 构建 + 测试**

Run: 同上 + `dotnet build Seeing.Agent.slnx -v q --nologo -m:1`
Expected: PASS、0 error

- [ ] **Step 5: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Components/SessionToolbar.razor samples/Seeing.Agent.WebUI/Components/CollaborationDirectoriesModal.razor tests/apps/Seeing.Agent.WebUI.Tests/Components/SessionToolbarCollaborationContractTests.cs
git commit -m "feat(webui): manage collaboration directories from session toolbar"
```

---

### Task 7: 冷会话预热

**Files:**
- Modify: `src/capabilities/Seeing.Agent.Files/FileWorkspaceFactory.cs`
- Modify: `src/capabilities/Seeing.Agent.Files/Seeing.Agent.Files.csproj`
- Test: `tests/capabilities/Seeing.Agent.Files.Tests/FileWorkspaceFactoryTests.cs`

**Interfaces:**
- 构造函数新增 `ISessionManager sessions`（`Seeing.Session.Core`），插在 `ISessionRootSet roots` 之后。
- `ListAsync` 改为 `async Task<IReadOnlyList<IFileWorkspace>>`，先 `await _sessions.TryGetOrLoadAsync(sessionId, ct)`。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public async Task ListAsync_ColdSession_ShouldLoadBeforeReadingRoots()
{
    var loaded = false;
    var roots = new CallbackRootSet(() => loaded);
    var sessions = new Mock<ISessionManager>();
    sessions.Setup(s => s.TryGetOrLoadAsync("s1", It.IsAny<CancellationToken>()))
            .Callback(() => loaded = true)
            .ReturnsAsync((SessionData?)null);

    var factory = CreateWith(roots, sessions.Object);
    await factory.ListAsync("s1");

    loaded.Should().BeTrue("根集合是只读内存，必须先预热会话");
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet vstest tests/capabilities/Seeing.Agent.Files.Tests/bin/Debug/net10.0/Seeing.Agent.Files.Tests.dll --TestCaseFilter:"FullyQualifiedName~ListAsync_ColdSession"`
Expected: 编译失败（构造函数无 `ISessionManager`）

- [ ] **Step 3: 实现**

- `csproj` 加 `ProjectReference` 到 `..\..\primitives\Seeing.Session\Seeing.Session.csproj`。
- 构造函数保存 `_sessions`；`ListAsync` 开头 `await _sessions.TryGetOrLoadAsync(sessionId, ct)`，其余逻辑不变。
- 测试 helper `Create` / `CreateMulti` 传入 `Mock<ISessionManager>.Object`（默认返回 null）。

- [ ] **Step 4: 运行确认通过**

Run: `dotnet vstest tests/capabilities/Seeing.Agent.Files.Tests/bin/Debug/net10.0/Seeing.Agent.Files.Tests.dll`
Expected: 全绿（含新用例）

- [ ] **Step 5: 提交**

```bash
git add src/capabilities/Seeing.Agent.Files tests/capabilities/Seeing.Agent.Files.Tests/FileWorkspaceFactoryTests.cs
git commit -m "fix(files): warm up session before reading roots"
```

---

### Task 8: 守卫与全量验证

**Files:**
- Modify: `tests/spine/Seeing.Agent.Invariants.Tests/Invariants/PathResolutionInvariantTests.cs`

- [ ] **Step 1: 加分层守卫**

```csharp
[Fact]
public void FileWorkbench_ShouldNotDependOnCollaborationWritePort()
{
    var dir = Path.Combine(FindRepoRoot(), "samples", "Seeing.Agent.WebUI.Files");
    var hits = Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
        .Where(f => f.EndsWith(".razor") || f.EndsWith(".cs"))
        .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                 && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
        .Where(f => File.ReadAllText(f).Contains("ICollaborationDirectories", StringComparison.Ordinal))
        .Select(f => Path.GetRelativePath(dir, f))
        .ToArray();

    hits.Should().BeEmpty("协同目录是会话功能，文件工作台只能消费只读根列表");
}
```

- [ ] **Step 2: 运行守卫**

Run: `dotnet vstest tests/spine/Seeing.Agent.Invariants.Tests/bin/Debug/net10.0/Seeing.Agent.Invariants.Tests.dll`
Expected: PASS

- [ ] **Step 3: 全量串行测试**

```powershell
$dlls = Get-ChildItem tests -Recurse -Filter "*.Tests.dll" |
  Where-Object { $_.FullName -match "\\bin\\Debug\\net10\.0\\" -and $_.FullName -notmatch "\\ref\\" }
foreach ($d in $dlls) { dotnet vstest $d.FullName }
```
Expected: 57 个项目全部 0 失败（Browser 的 11 个条件跳过不计失败）

- [ ] **Step 4: 提交**

```bash
git add tests/spine/Seeing.Agent.Invariants.Tests
git commit -m "test: guard file workbench against collaboration write port"
```
