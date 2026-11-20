# 单一 cwd 与协同目录 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 一次执行只有一个 cwd（本期恒等于工作区根）并显式透传到所有工具；新增会话级协同目录（持久化、可读写、可浏览、上下文可见）；工作区切换后所有派生状态从零重建。

**Architecture:** `ExecutionRunner` 计算一次 cwd → `ToolInvocation`（唯一执行事实载体）→ `IToolManager` → `ToolContext` 投影；`IWorkspacePathGate.ResolveAllowed` 把"解析 + 授权"合成一个入口；目录判定收敛到 `ISessionRootSet`（`IsAuthorized`/`IsTrusted`），协同目录写收敛到 `ICollaborationDirectories`；多根文件工作台由 `IFileWorkspaceFactory` 统一提供列表/路由/精确取。

**Tech Stack:** .NET 10 / C#、ASP.NET Core、Blazor、Quartz.NET、xUnit + FluentAssertions + Moq。

**Spec:** `docs/superpowers/specs/2026-11-19-single-cwd-source-design.md`

## Global Constraints

- 能力包只依赖 `Abstractions` + 原语 + `support` 层；契约改动落在 `src/abstractions/`。
- 路径边界判定只用 `PathBoundary`；原子写只用 `AtomicFileWrite`。
- 工具是 Singleton：禁止在构造期捕获 cwd / 工作区路径 / 会话数据。
- 测试禁止固定时长等待（`Task.Delay` / `Thread.Sleep`）。
- 每个任务结束必须 `dotnet build` 通过；测试统一用 VSTest：
  ```bash
  dotnet build tests/<proj>
  dotnet vstest tests/<proj>/bin/Debug/net10.0/<Asm>.dll --TestCaseFilter:"FullyQualifiedName~<Class>"
  ```
- 提交信息前缀：`feat:` / `fix:` / `refactor:` / `test:`。
- **禁止**：给 `IToolManager.ExecuteAsync` 追加第 9 个可选参数；给 `IToolContext` 追加"只有某条链路会填"的散字段；给工厂加 `RefCount`。

## Review Focus

1. **历史会话 cwd 偏离工作区根**：必须收敛并回写，不能继续参与解析 → Task 2。
2. **协同目录失效**：UI 灰显、工具明确报错，不回落主根 → Task 8 / 11。
3. **多根后 `GetAbsolutePath` 错 RootId**：会静默覆盖主根同名文件 → Task 11。
4. **`auto_approve` 下 agent 自加协同目录**：`RequireInteraction` 必须短路 `AutoApproveAll`，`Once` 不落盘 → Task 9。
5. **`explore`/`plan` 的文件 Deny 在协同目录内被门闸短路**：Deny 必须先于自动放行 → Task 5 / 8。
6. **空协同目录**：首轮必须建立基线并渲染"（无）" → Task 7。

---

### Task 1: IFileSystem 双参解析（阶段 1）

**Files:**
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Execution/IFileSystem.cs:71-72`
- Modify: `src/capabilities/Seeing.IO.Local/LocalFileSystem.cs:124`
- Test: `tests/capabilities/Seeing.IO.Local.Tests/FileSystemResolveTests.cs`（新建）

**Interfaces:**
- Produces: `string GetFullPath(string path, string baseDirectory)`；保留单参（只加 `Obsolete` 警告）。

- [ ] **Step 1: 写失败测试**

```csharp
using FluentAssertions;
using Seeing.IO.Local;
using Xunit;

namespace Seeing.IO.Local.Tests;

public class FileSystemResolveTests
{
    [Fact]
    public void GetFullPath_RelativePath_UsesGivenBase()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "seeing_base_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);

        var actual = new LocalFileSystem().GetFullPath("sub/a.txt", baseDir);

        actual.Should().Be(Path.GetFullPath(Path.Combine(baseDir, "sub", "a.txt")));
    }

    [Fact]
    public void GetFullPath_AbsolutePath_IgnoresBase()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "x.txt");
        new LocalFileSystem().GetFullPath(absolute, "C:\\other").Should().Be(Path.GetFullPath(absolute));
    }
}
```

- [ ] **Step 2: 运行，确认失败**

```bash
dotnet build tests/capabilities/Seeing.IO.Local.Tests
dotnet vstest tests/capabilities/Seeing.IO.Local.Tests/bin/Debug/net10.0/Seeing.IO.Local.Tests.dll --TestCaseFilter:"FullyQualifiedName~FileSystemResolveTests"
```

- [ ] **Step 3: 实现**

```csharp
// IFileSystem
string GetFullPath(string path, string baseDirectory);

[Obsolete("业务路径解析必须显式给出基准目录；进程 CWD 不是业务基准。")]
string GetFullPath(string path);

// LocalFileSystem
public string GetFullPath(string path, string baseDirectory) => Path.GetFullPath(path, baseDirectory);

[Obsolete("业务路径解析必须显式给出基准目录；进程 CWD 不是业务基准。")]
public string GetFullPath(string path) => Path.GetFullPath(path);
```

- [ ] **Step 4: 运行通过并提交**

```bash
git add -A
git commit -m "feat: add explicit-base GetFullPath overload"
```

---

### Task 2: ToolInvocation + cwd 唯一计算点 + 进程 CWD 开关

**Files:**
- Create: `src/abstractions/Seeing.Agent.Abstractions/Tools/ToolInvocation.cs`
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Tools/ITool.cs`
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Tools/IToolManager.cs`
- Modify: `src/spine/Seeing.Agent.Core/Tools/ToolManager.cs`
- Modify: `src/spine/Seeing.Agent.Core/Core/ToolCallInvoker.cs`
- Create: `src/hosting/Seeing.Agent.Hosting/Execution/ExecutionCwdResolver.cs`
- Create: `src/hosting/Seeing.Agent.Hosting/Execution/ExecutionScopeInitializer.cs`
- Modify: `src/hosting/Seeing.Agent.Hosting/Execution/ExecutionRunner.cs`
- Modify: `src/spine/Seeing.Agent.Core/Configuration/WorkspaceProvider.cs:214-253`
- Test: `tests/spine/Seeing.Agent.Tests/Tools/ToolInvocationTests.cs`
- Test: `tests/hosting/Seeing.Agent.Hosting.Tests/Execution/ExecutionCwdResolverTests.cs`
- Test: `tests/hosting/Seeing.Agent.Hosting.Tests/Execution/ProcessCwdSwitchTests.cs`

**Interfaces:**
- Produces: `ToolInvocation`；`IToolManager.ExecuteAsync(ToolCall, ToolInvocation, CancellationToken)`；`ExecutionCwdResolver.Resolve`；`WorkspaceProvider.MutatesProcessCwd`；`ExecutionScopeInitializer.Initialize`。

- [ ] **Step 1: 写失败测试（invocation 透传）**

```csharp
[Fact]
public async Task ExecuteAsync_Invocation_ShouldProjectIntoContext()
{
    var manager = new ToolManager(NullLogger<ToolManager>.Instance, new HookManager(NullLogger<HookManager>.Instance));
    var tool = new CapturingTool();
    await manager.RegisterToolAsync(tool);

    var invocation = new ToolInvocation(
        "s1", "D:/work", null, null, null, null, [], null);

    await manager.ExecuteAsync(
        new ToolCall { Id = "c1", Function = new FunctionCall { Name = "capture", Arguments = "{}" } },
        invocation,
        TestContext.Current.CancellationToken);

    tool.Captured!.WorkingDirectory.Should().Be("D:/work");
    tool.Captured.SessionId.Should().Be("s1");
}
```

- [ ] **Step 2: 写失败测试（cwd 收敛 + 开关）**

```csharp
[Fact]
public void Resolve_AnyDivergence_ShouldConvergeToRoot()
{
    var root = NewRoot();
    var result = ExecutionCwdResolver.Resolve("D:/else", "D:/other", new WorkspaceProvider(root));

    result.Path.Should().Be(Path.GetFullPath(root));
    result.NeedsSessionWriteBack.Should().BeTrue();
}

[Fact]
public void Resolve_Unresolved_ShouldThrow()
{
    var act = () => ExecutionCwdResolver.Resolve(null, null, WorkspaceProvider.CreateUnresolved());
    act.Should().Throw<InvalidOperationException>();
}

[Fact]
public void SwitchWorkspace_WithMutatesProcessCwdFalse_ShouldNotChangeProcessCwd()
{
    var before = Directory.GetCurrentDirectory();
    var provider = new WorkspaceProvider(NewRoot()) { MutatesProcessCwd = false };

    provider.SetWorkspaceRoot(NewRoot());

    Directory.GetCurrentDirectory().Should().Be(before);
}
```

- [ ] **Step 3: 运行，确认失败**

```bash
dotnet build tests/spine/Seeing.Agent.Tests
dotnet build tests/hosting/Seeing.Agent.Hosting.Tests
```

- [ ] **Step 4: 实现**

`ToolInvocation`：

```csharp
public sealed record ToolInvocation(
    string SessionId,
    string Cwd,
    AgentDefinition? Agent,
    IPermissionAuthorizer? Authorizer,
    IExecutionToolScope? ToolScope,
    SkillCatalogSnapshot? SkillSnapshot,
    IReadOnlyList<CollaborationDirectory> Collaborations,
    Func<IMessageEvent, ValueTask>? Emit);
```

`IToolManager.ExecuteAsync(ToolCall toolCall, ToolInvocation invocation, CancellationToken cancellationToken = default)`；`ToolManager` 用它投影 `ToolContext`（`SessionId/CallId/Agent/Authorizer/ToolScope/SkillSnapshot/WorkingDirectory/Services`）。

`ToolCallInvoker` 构造 `ToolInvocation`（从 `AgentContext` + `context.WorkingDirectory` + 会话协同目录）。

`ExecutionCwdResolver`：internal record `CwdResolution(string Path, bool NeedsSessionWriteBack, string? Warning)`；`Resolve` 规则见 spec §5.1（未决议抛；任何偏离收敛到项目根；不读磁盘）；`SamePath` 用 `PathBoundary.PathComparison` + `PathBoundary.Canonicalize`。

`WorkspaceProvider`：新增 `public bool MutatesProcessCwd { get; set; }`；`SetWorkspaceRootInternal` 只在为 true 时 `Directory.SetCurrentDirectory`。

`ExecutionScopeInitializer.Initialize(IContextExecutionScope scope, ToolInvocation invocation)`：一次填 `SessionId/Cwd/Collaborations/SkillSnapshot`；`ExecutionRunner` 与后续 `SchedulerContextAssembly` 共用。

`ExecutionRunner`：删除 `:151-157` 独立计算，改在 `BuildExecutionContextAsync` 用 resolver；`NeedsSessionWriteBack` 时回写并 `SaveAsync`。

- [ ] **Step 5: 运行通过并提交**

```bash
dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~ToolInvocationTests"
dotnet vstest tests/hosting/Seeing.Agent.Hosting.Tests/bin/Debug/net10.0/Seeing.Agent.Hosting.Tests.dll --TestCaseFilter:"FullyQualifiedName~ExecutionCwdResolverTests|FullyQualifiedName~ProcessCwdSwitchTests"
git add -A
git commit -m "feat: pass execution facts via ToolInvocation and guard process cwd"
```

---

### Task 3: ResolveAllowed（解析 + 授权合一）+ 文件工具

**Files:**
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Permissions/IWorkspacePathGate.cs`
- Modify: `src/spine/Seeing.Agent.Core/Core/Permission/WorkspacePathGate.cs`
- Modify: `src/support/Seeing.Agent.Tools.Support/BuiltInToolBase.cs`
- Modify: `src/capabilities/Seeing.Agent.Tools.FileSystem/{WriteTool,EditTool,ReadTool,DeleteTool,OpenFileTool,AddWorkspacePathTool}.cs`
- Modify: `tests/capabilities/Seeing.Agent.Tools.FileSystem.Tests/*`（`AllowAllPathGate` 等桩补新方法）
- Test: `tests/capabilities/Seeing.Agent.Tools.FileSystem.Tests/ToolWorkspaceCwdTests.cs`

**Interfaces:**
- Produces: `string? ResolveAllowed(string sessionId, string path, string? baseDirectory, out string fullPath)`；工具不再有 `ToolPath` 约定。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public async Task Write_RelativePath_UsesContextWorkingDirectory()
{
    var dir = NewDir();
    var tool = new WriteTool(NullLogger<WriteTool>.Instance, new LocalFileSystem(), AllowAllPathGate.Instance, new ProcessPathWriteLock());

    var result = await tool.ExecuteAsync(
        JsonSerializer.SerializeToElement(new { path = "a.txt", content = "x" }),
        new ToolContext { WorkingDirectory = dir });

    result.Success.Should().BeTrue();
    File.Exists(Path.Combine(dir, "a.txt")).Should().BeTrue();
}

[Fact]
public async Task Write_MissingWorkingDirectory_ShouldFail()
{
    var tool = new WriteTool(NullLogger<WriteTool>.Instance, new LocalFileSystem(), AllowAllPathGate.Instance, new ProcessPathWriteLock());

    var result = await tool.ExecuteAsync(
        JsonSerializer.SerializeToElement(new { path = "a.txt", content = "x" }),
        new ToolContext());

    result.Success.Should().BeFalse();
    result.Error.Should().Contain("工作目录");
}
```

- [ ] **Step 2: 运行，确认失败**

```bash
dotnet build tests/capabilities/Seeing.Agent.Tools.FileSystem.Tests
dotnet vstest tests/capabilities/Seeing.Agent.Tools.FileSystem.Tests/bin/Debug/net10.0/Seeing.Agent.Tools.FileSystem.Tests.dll --TestCaseFilter:"FullyQualifiedName~ToolWorkspaceCwdTests"
```

- [ ] **Step 3: 实现**

`IWorkspacePathGate` 增加：

```csharp
string? ResolveAllowed(string sessionId, string path, string? baseDirectory, out string fullPath);
```

`WorkspacePathGate`：base 为空 → 返回错误；`Path.GetFullPath(path, base)` → `IsAuthorized`；不通过返回错误文案。`EnsureAllowed`/`EnsureWithinWorkspace` 保留为薄壳（委派 `IsAuthorized`/`IsTrusted`，Task 8 完成）。

`BuiltInToolBase`：删除 `_workingDirectory`、构造参数、`ResolvePath`。

六个工具：

```csharp
var error = _pathGate.ResolveAllowed(context.SessionId, filePath, context.WorkingDirectory, out var fullPath);
if (error is not null) return Failure(error);
```

- [ ] **Step 4: 运行通过并提交**

```bash
dotnet vstest tests/capabilities/Seeing.Agent.Tools.FileSystem.Tests/bin/Debug/net10.0/Seeing.Agent.Tools.FileSystem.Tests.dll --TestCaseFilter:"FullyQualifiedName~ToolWorkspaceCwdTests"
git add -A
git commit -m "refactor: combine path resolution and authorization in ResolveAllowed"
```

---

### Task 4: glob / grep / bash / git / speech

**Files:**
- Modify: `src/capabilities/Seeing.Agent.Tools.FileSystem/{GlobTool,GrepTool}.cs`
- Modify: `src/capabilities/Seeing.Agent.Tools.Shell/BashTool.cs`
- Modify: `src/capabilities/Seeing.Agent.Tools.Git/{GitService,Git*Tool}.cs`
- Modify: `src/capabilities/Seeing.Agent.Tools.Speech/TranscribeAudioTool.cs`
- Test: `tests/capabilities/Seeing.Agent.Tools.Shell.Tests/BashToolCwdTests.cs`
- Test: `tests/capabilities/Seeing.Agent.Tools.FileSystem.Tests/GlobGrepCwdTests.cs`

**Interfaces:**
- Consumes: `ResolveAllowed`、`ToolContext.WorkingDirectory`。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public async Task Bash_NoWorkdir_UsesContextWorkingDirectory()
{
    var dir = NewDir();
    var result = await CreateTool().ExecuteAsync(
        JsonSerializer.SerializeToElement(new { command = "pwd", description = "pwd" }),
        new ToolContext { WorkingDirectory = dir });

    result.Success.Should().BeTrue();
    result.Output.Should().Contain(dir);
}
```

- [ ] **Step 2: 运行，确认失败**

```bash
dotnet build tests/capabilities/Seeing.Agent.Tools.Shell.Tests
dotnet vstest tests/capabilities/Seeing.Agent.Tools.Shell.Tests/bin/Debug/net10.0/Seeing.Agent.Tools.Shell.Tests.dll --TestCaseFilter:"FullyQualifiedName~BashToolCwdTests"
```

- [ ] **Step 3: 实现**

- `BashTool`：`workdir = args.workdir ?? context.WorkingDirectory`；经 `ResolveAllowed` 校验（越界 → `Failure`）。
- `GlobTool`/`GrepTool`：默认根 `context.WorkingDirectory`；相对路径经 `ResolveAllowed`。
- `GitService.ExecuteAsync(..., string workingDirectory)`；`Git*Tool` 传 `context.WorkingDirectory` 并校验。
- `TranscribeAudioTool`：`ResolveAllowed`。

- [ ] **Step 4: 运行通过并提交**

```bash
git add -A
git commit -m "refactor: use execution cwd for shell, git, glob, grep and speech"
```

---

### Task 5: 权限决策三段式 + 相对 Resource 绝对化

**Files:**
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Permissions/PermissionRequest.cs`
- Modify: `src/spine/Seeing.Agent.Core/Tools/ToolManager.cs`
- Modify: `src/spine/Seeing.Agent.Core/Core/Permission/PermissionService.cs:115-241,289-341`
- Modify: `src/spine/Seeing.Agent.Core/Core/Permission/PermissionContext.cs:28`
- Test: `tests/spine/Seeing.Agent.Tests/Permissions/PermissionCwdTests.cs`

**Interfaces:**
- Produces: `PermissionRequest.WorkingDirectory`；`PermissionService` 三段式：**否决（Deny 规则）→ 自动放行块（`autoApproveAllowed`）→ 询问**。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public async Task Authorize_RequireInteraction_ShouldNotBeAutoApprovedByToggle()
{
    // AutoApproveAll + RequireInteraction=true → Expect Deny(NoChannel) 或进入询问，不是 Allow
}

[Fact]
public async Task Authorize_FileDenyRule_ShouldBeatGateShortCircuit()
{
    // gate 放行 + Deny(File,"*") → Expect Deny
}
```

- [ ] **Step 2: 运行，确认失败**

```bash
dotnet build tests/spine/Seeing.Agent.Tests
dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~PermissionCwdTests"
```

- [ ] **Step 3: 实现**

- `PermissionRequest` 加 `WorkingDirectory`。
- `ToolManager` 预检：相对 `check.Resource` 用 `workingDirectory` 绝对化；填 `PermissionRequest.WorkingDirectory`。
- `PermissionService` 结构改为：

```
1) Deny 规则（含文件类）→ 直接 Deny
2) autoApproveAllowed = !RequireInteraction && toggle != Ask
   2a) gateAllows && autoApproveAllowed → Allow
   2b) grant 命中 && autoApproveAllowed → Allow（Deny 已在上一步排除）
   2c) 规则 Allow（非资源类）&& autoApproveAllowed → Allow
   2d) toggle Allow && autoApproveAllowed → Allow
   2e) channel TryAutoApprove && autoApproveAllowed → Allow
3) 询问（无通道 → Deny(NoChannel)）
```

- `BuildPolicyContext` 用 `request.WorkingDirectory ?? ""`；`ResolveResourceDirectory` 用它兜底；`PermissionContext` 默认空串。

- [ ] **Step 4: 运行通过并提交**

```bash
git add -A
git commit -m "refactor: make permission decision a deny-first three-stage pipeline"
```

---

### Task 6: 非执行链 cwd

**Files:**
- Modify: `src/capabilities/Seeing.Agent.Scheduler/Jobs/{AgentJob,SchedulerContextAssembly}.cs`
- Modify: `src/capabilities/Seeing.Agent.Skills/SkillLoader.cs:97`
- Modify: `src/capabilities/Seeing.Agent.Mcp/WorkspaceSeeingDirectories.cs:15`
- Modify: `src/capabilities/Seeing.Agent.Acp/{Filesystem/AcpFileSystemBridge,Execution/AcpPassthroughExecutor,Terminal/AcpTerminalBridge,Client/SeeingAcpClient}.cs`
- Modify: `src/capabilities/Seeing.Agent.Files/FileWorkspace.cs:62-71`
- Test: `tests/capabilities/Seeing.Agent.Scheduler.Tests/Jobs/AgentJobCwdTests.cs`
- Test: `tests/capabilities/Seeing.Agent.Files.Tests/FileWorkspacePathTests.cs`

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void GetResourceId_PathOutsideRoot_ShouldThrow()
{
    var ws = NewWorkspace(@"D:\root");
    var act = () => ws.GetResourceId(@"D:\other\a.txt");
    act.Should().Throw<ArgumentException>();
}
```

- [ ] **Step 2: 运行，确认失败**

```bash
dotnet build tests/capabilities/Seeing.Agent.Files.Tests
dotnet vstest tests/capabilities/Seeing.Agent.Files.Tests/bin/Debug/net10.0/Seeing.Agent.Files.Tests.dll --TestCaseFilter:"FullyQualifiedName~FileWorkspacePathTests"
```

- [ ] **Step 3: 实现**

`AgentJob` 构造 `ToolInvocation`（`session.WorkingDirectory ?? GetProjectRoot()`）；`SchedulerContextAssembly` 经 `ExecutionScopeInitializer`；`SkillLoader` 回落工作区根；`WorkspaceSeeingDirectories` 空 root 抛；ACP 三处兜底改工作区根并删除 `_defaultWorkingDirectory`；`FileWorkspace.GetResourceId` 以 `Root.RootPath` 为 base 且越界抛。

- [ ] **Step 4: 运行通过并提交**

```bash
git add -A
git commit -m "fix: remove process-CWD fallbacks from non-execution paths"
```

---

### Task 7: 协同目录数据层 + ICollaborationDirectories + ISessionRootSet + 上下文源

**Files:**
- Modify: `src/primitives/Seeing.Session/Core/SessionData.cs:70,531-582`
- Modify: `src/primitives/Seeing.Session/Core/SessionSummary.cs`
- Modify: `src/primitives/Seeing.Session/Management/{SessionGroupManager,SessionForker}.cs`
- Modify: `src/primitives/Seeing.Session/Storage/FileSessionStore.cs:552-595`
- Create: `src/abstractions/Seeing.Agent.Abstractions/Directories/ICollaborationDirectories.cs`
- Create: `src/abstractions/Seeing.Agent.Abstractions/Directories/ISessionRootSet.cs`
- Create: `src/spine/Seeing.Agent.Core/Directories/CollaborationDirectories.cs`
- Create: `src/spine/Seeing.Agent.Core/Directories/SessionRootSet.cs`
- Modify: `src/abstractions/.../Context/IContextExecutionScope.cs`
- Modify: `src/spine/Seeing.Agent.Core/Context/ContextExecutionScope.cs`
- Create: `src/spine/Seeing.Agent.Core/Context/CollaborationDirectoriesContextSource.cs`
- Modify: `src/spine/Seeing.Agent.Core/Extensions/ServiceCollectionExtensions.cs:800-806`
- Modify: `src/hosting/.../Execution/ExecutionRunner.cs`（填 scope）
- Modify: `src/capabilities/Seeing.Agent.Scheduler/Jobs/SchedulerContextAssembly.cs`（填 scope）
- Test: `tests/primitives/Seeing.Session.Tests/Core/CollaborationDirectoriesTests.cs`
- Test: `tests/spine/Seeing.Agent.Tests/Context/CollaborationDirectoriesContextSourceTests.cs`
- Test: `tests/spine/Seeing.Agent.Tests/Directories/SessionRootSetTests.cs`

**Interfaces:**
- Produces: `CollaborationDirectory(Path, Note)`；`SessionData.CollaborationDirectories`；`ICollaborationDirectories`；`ISessionRootSet`（`Roots`/`IsAuthorized`/`IsTrusted`）；`IContextExecutionScope.CollaborationDirectories`；`CollaborationDirectoriesContextSource`。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public async Task ReadAsync_EmptyList_ShouldReturnValueAndRenderNone()
{
    var source = new CollaborationDirectoriesContextSource(new FakeExecutionScope([]));
    var value = await source.ReadAsync(TestContext.Current.CancellationToken);

    value.State.Should().Be(ContextValueState.Value);
    source.RenderInitial(value).Should().Contain("（无）");
}

[Fact]
public void IsTrusted_ShouldNotIncludeTemporaryGrant()
{
    // 根 = 项目根；白名单含 D:\x → IsAuthorized(D:\x)=true，IsTrusted(D:\x)=false
}
```

- [ ] **Step 2: 运行，确认失败**

```bash
dotnet build tests/primitives/Seeing.Session.Tests
dotnet build tests/spine/Seeing.Agent.Tests
```

- [ ] **Step 3: 实现**

1. `SessionData` 字段 + `CollaborationDirectory` 顶层 record；`Clone` 深拷贝；`SessionSummary`/`CreateChild`/`CreateHandoffSuccessorAsync`/`SessionForker`/`FileSessionStore` 6 处同步。
2. `ICollaborationDirectories` 实现：读写 `SessionData`，绝对化 + realpath + 去重；失效不删。
3. `ISessionRootSet` 实现：`Roots` = 项目根 + 协同目录；`IsAuthorized` = 根 ∪ `IPermissionGrantStore`；`IsTrusted` = 仅根。冷会话内部 `TryGetOrLoadAsync`。
4. `IContextExecutionScope` / `ContextExecutionScope` 加字段；5 个测试替身同步；`ExecutionScopeInitializer` 填。
5. `CollaborationDirectoriesContextSource`（scoped；空列表 `Value`；`Exists` 实时探针；源内截断；不渲染 cwd）。
6. `AddContextPipeline` 注册 scoped 源。

- [ ] **Step 4: 运行通过并提交**

```bash
git add -A
git commit -m "feat: add collaboration directories with single root authority"
```

---

### Task 8: 门闸适配 + 附件准入

**Files:**
- Modify: `src/spine/Seeing.Agent.Core/Core/Permission/WorkspacePathGate.cs`
- Modify: `src/capabilities/Seeing.Agent.Tools.ChannelMessaging/ChannelAttachmentNormalizer.cs`
- Modify: `tests/spine/Seeing.Agent.Tests/Permissions/WorkspacePathGateSessionRootTests.cs:98-119`
- Test: `tests/spine/Seeing.Agent.Tests/Permissions/SessionRootGateTests.cs`

**Interfaces:**
- Consumes: `ISessionRootSet`。
- Produces: `EnsureAllowed` → `IsAuthorized`；`EnsureWithinWorkspace` → `IsTrusted`。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void EnsureAllowed_CollaborationDirectory_ShouldAllow() { /* ... */ }

[Fact]
public void EnsureWithinWorkspace_TemporaryGrantOnly_ShouldDeny() { /* 白名单不泄漏 */ }
```

- [ ] **Step 2: 运行，确认失败**

```bash
dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~SessionRootGateTests"
```

- [ ] **Step 3: 实现**

`WorkspacePathGate` 注入 `ISessionRootSet`，两个方法变薄壳；删除 `_sessionWorkspace` 与"会话 cwd 作为根"分支；附件改 `EnsureWithinWorkspace`（→ `IsTrusted`）。同步更新锁定测试的协同目录部分，保留"白名单不泄漏"断言。

- [ ] **Step 4: 运行通过并提交**

```bash
git add -A
git commit -m "feat: route gate and attachments through session root authority"
```

---

### Task 9: add_collaboration_directory

**Files:**
- Create: `src/capabilities/Seeing.Agent.Tools.FileSystem/AddCollaborationDirectoryTool.cs`
- Modify: `src/capabilities/Seeing.Agent.Tools.FileSystem/FileSystemModule.cs`
- Modify: `src/spine/Seeing.Agent.Core/Core/Permission/PermissionKindMapper.cs`
- Test: `tests/capabilities/Seeing.Agent.Tools.FileSystem.Tests/AddCollaborationDirectoryToolTests.cs`

**Interfaces:**
- Consumes: `ToolContext.PermissionAuthorizer`、`ICollaborationDirectories`。
- Produces: 工具 `add_collaboration_directory(path, note?)`。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public async Task Execute_NoAuthorizer_ShouldFail() { /* ... */ }

[Fact]
public async Task Execute_OnceScope_ShouldNotPersist() { /* ... */ }

[Fact]
public async Task Execute_SessionDirectoryScope_ShouldPersist() { /* ... */ }
```

- [ ] **Step 2: 运行，确认失败**

```bash
dotnet build tests/capabilities/Seeing.Agent.Tools.FileSystem.Tests
```

- [ ] **Step 3: 实现**

`PermissionAuthorizer == null` → `Failure`；`RequireInteraction = true`；仅 `Allow && Scope != Once` 调 `ICollaborationDirectories.AddAsync`；同路径去重；`PermissionKindMapper` 加 `filesystem.collaboration_directory`。

- [ ] **Step 4: 运行通过并提交**

```bash
git add -A
git commit -m "feat: add add_collaboration_directory tool with interaction requirement"
```

---

### Task 10: 多根文件工作台（无引用计数）

**Files:**
- Create: `src/abstractions/Seeing.Agent.Abstractions/Files/FileWorkspaceRoot.cs`
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Files/IFileWorkspace.cs`
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Files/IFileWorkspaceFactory.cs`
- Modify: `src/capabilities/Seeing.Agent.Files/FileWorkspace.cs`
- Modify: `src/capabilities/Seeing.Agent.Files/FileWorkspaceFactory.cs`
- Test: `tests/capabilities/Seeing.Agent.Files.Tests/FileWorkspaceFactoryMultiRootTests.cs`

**Interfaces:**
- Produces: `FileWorkspaceRoot`；`IFileWorkspace.Root`；`IFileWorkspaceFactory.ListAsync/GetAsync/ResolveAsync(absolutePath)/Release`。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public async Task ListAsync_ShouldReturnProjectAndCollaborations()
{
    // 会话 2 个协同目录 → 3 个根
}

[Fact]
public async Task Release_ShouldClearAllRoots_Idempotently()
{
    // List → Release → Release → 断言缓存空
}
```

- [ ] **Step 2: 运行，确认失败**

```bash
dotnet build tests/capabilities/Seeing.Agent.Files.Tests
```

- [ ] **Step 3: 实现**

- `FileWorkspaceRoot` + `FileWorkspaceRootKind`；`IFileWorkspace.Root`（`RootId`/`RootPath` 转发）。
- 工厂 `Entry(Dictionary<string, IFileWorkspace>)`，无 `RefCount`；`ListAsync` 增量对齐；`GetAsync(rootId)`；`ResolveAsync(absolutePath)` 最长前缀 + `PathBoundary`；`Release` 幂等全清。
- 根集合来源 `ISessionRootSet.Roots`。

- [ ] **Step 4: 运行通过并提交**

```bash
git add -A
git commit -m "feat: multi-root file workspace without reference counting"
```

---

### Task 11: 多根路由与消费方

**Files:**
- Modify: `src/capabilities/Seeing.Agent.Files/FileWorkspace.cs:62-71`
- Modify: `samples/Seeing.Agent.WebUI/Services/{AgentFileChangeBridge,UploadedFileService}.cs`
- Test: `tests/capabilities/Seeing.Agent.Files.Tests/FileWorkspaceRoutingTests.cs`

**Interfaces:**
- Consumes: `IFileWorkspaceFactory.ResolveAsync(sessionId, absolutePath)`。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void GetAbsolutePath_ForeignRootId_ShouldThrow() { /* ... */ }

[Fact]
public void GetAbsolutePath_EscapingRelativePath_ShouldThrow() { /* ... */ }
```

- [ ] **Step 2: 运行，确认失败**

```bash
dotnet vstest tests/capabilities/Seeing.Agent.Files.Tests/bin/Debug/net10.0/Seeing.Agent.Files.Tests.dll --TestCaseFilter:"FullyQualifiedName~FileWorkspaceRoutingTests"
```

- [ ] **Step 3: 实现**

`GetAbsolutePath` 校验 `RootId` + `PathBoundary`；`AgentFileChangeBridge` 用工厂路由；`UploadedFileService` 默认主根、可选 `rootId`。

- [ ] **Step 4: 运行通过并提交**

```bash
git add -A
git commit -m "fix: route file resources by root id"
```

---

### Task 12: WebUI 多根展示

**Files:**
- Modify: `samples/Seeing.Agent.WebUI.Files/Components/{FilePanelHost,FileEditor}.razor`
- Create: `samples/Seeing.Agent.WebUI.Files/Components/FileRootList.razor`
- Test: `tests/apps/Seeing.Agent.WebUI.Tests/Components/FileRootListContractTests.cs`

- [ ] **Step 1: 写契约测试**

```csharp
[Fact]
public void FilePanelHost_ShouldUseListAsync()
{
    var text = File.ReadAllText(FindRepoFile("samples/Seeing.Agent.WebUI.Files/Components/FilePanelHost.razor"));
    text.Should().Contain("ListAsync");
}
```

- [ ] **Step 2: 运行，确认失败**

```bash
dotnet build tests/apps/Seeing.Agent.WebUI.Tests
```

- [ ] **Step 3: 实现**

根列表组件（项目根 + 协同目录，失效灰显）；`FilePanelHost` 每根一个 facade；标签带根标识。

- [ ] **Step 4: 运行通过并提交**

```bash
git add -A
git commit -m "feat: show multiple roots in file workbench"
```

---

### Task 13: 工作区派生状态（Quartz 用户级、AgentManager 重建、tool-state）

**Files:**
- Modify: `src/capabilities/Seeing.Agent.Scheduler/{Configuration/SchedulerWorkspaceParticipant,Management/ScheduleManager,Extensions/SchedulerServiceCollectionExtensions}.cs`
- Create: `src/spine/Seeing.Agent.Core/Tools/ToolStateReloadHandler.cs`
- Modify: `src/spine/Seeing.Agent.Core/Configuration/{AgentManager,AgentManagerReloadHandler}.cs`
- Modify: `src/gateway/Seeing.Gateway.WeCom/WeComSessionTracker.cs`
- Modify: `src/gateway/Seeing.Gateway.QQ/QQSessionTracker.cs`
- Test: `tests/capabilities/Seeing.Agent.Scheduler.Tests/{ScheduleManagerWorkspaceTests,QuartzPersistenceTests}.cs`
- Test: `tests/spine/Seeing.Agent.Tests/{Tools/ToolStateReloadTests,Agents/AgentManagerResetTests}.cs`

**Interfaces:**
- Produces: `ScheduleManager.ResetForWorkspace()`；`AgentManager.ResetProjectOverridesAsync()`；`ToolStateReloadHandler`。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public async Task Switch_ShouldLoadNewJobsFile() { /* ... */ }

[Fact]
public async Task QuartzDb_ShouldStayInUserDirectory() { /* ... */ }

[Fact]
public async Task Switch_ShouldReloadToolState() { /* ... */ }
```

- [ ] **Step 2: 运行，确认失败**

```bash
dotnet build tests/capabilities/Seeing.Agent.Scheduler.Tests
dotnet build tests/spine/Seeing.Agent.Tests
```

- [ ] **Step 3: 实现**

- `quartz.db` 改 `~/.seeing/scheduler/quartz.db`（`Scheduler.Persistence.ConnectionString` 显式覆盖）；`AddQuartz` 原样保留；删除"按工作区重建 factory"的改造。
- `ScheduleManager.ResetForWorkspace()`；`SchedulerWorkspaceParticipant` 编排"停 → 清 → 重建 → 起"；不注册 handler。
- `ToolStateReloadHandler`：清空 `_projectDisabledTools` 再 `LoadToolStateAsync`。
- `AgentManager.ResetProjectOverridesAsync()`：从 `_configCache` 的 `Project:` 键反向清理，不新增字段。
- WeCom 路径现算；QQ 参与者 `OnApplied` 清空映射。

- [ ] **Step 4: 运行通过并提交**

```bash
git add -A
git commit -m "fix: rebuild workspace-derived state on switch"
```

---

### Task 14: 切换流程修复

**Files:**
- Modify: `src/hosting/Seeing.Agent.Hosting/Execution/{WorkspaceSwitcher,WorkspaceReloadAdapters}.cs`
- Modify: `src/spine/Seeing.Agent.Core/Configuration/ReloadOrchestrator.cs`
- Test: `tests/hosting/Seeing.Agent.Hosting.Tests/Execution/WorkspaceSwitcherSuppressionTests.cs`
- Test: `tests/spine/Seeing.Agent.Tests/Configuration/ReloadOrchestratorWorkspaceTests.cs`

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public async Task Switch_ShouldNotTriggerThirdReload() { /* 计数 == 2 */ }

[Fact]
public async Task ConfigChangedDuringSuppression_ShouldCompensate() { /* ... */ }

[Fact]
public void AutoReload_ShouldUseOldWorkspaceFromEvent() { /* ... */ }
```

- [ ] **Step 2: 运行，确认失败**

```bash
dotnet build tests/hosting/Seeing.Agent.Hosting.Tests
```

- [ ] **Step 3: 实现**

抑制范围覆盖整个 `ApplyAsync`；抑制期置 `_suppressedSignal`，释放时补一轮；`OnWorkspaceChanged` 暂存 `e.OldWorkspace` 跨去抖传给 `WorkspaceChange`。

- [ ] **Step 4: 运行通过并提交**

```bash
git add -A
git commit -m "fix: keep reload signals during workspace switch suppression"
```

---

### Task 15: 收口（Obsolete error + 不变量测试）

**Files:**
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Execution/IFileSystem.cs`
- Modify: 所有剩余单参 `GetFullPath` 调用点
- Create: `tests/spine/Seeing.Agent.Invariants.Tests/Invariants/PathResolutionInvariantTests.cs`
- Modify: `tests/spine/Seeing.Agent.Invariants.Tests/Invariants/ExecutionWorldTests.cs:31`
- Modify: `tests/capabilities/Seeing.IO.Local.Tests/LocalExecutionWorldTests.cs:24`

- [ ] **Step 1: 写守卫测试**

```csharp
[Fact]
public void Src_ShouldOnlyMutateProcessCwdInWorkspaceProvider()
{
    var hits = ScanForToken(FindRepoRoot(), "SetCurrentDirectory")
        .Where(h => !h.Contains("WorkspaceProvider.cs"));
    hits.Should().BeEmpty();
}

[Fact]
public void Src_ShouldNotContain_workingDirectoryFieldOrRefCount()
{
    ScanForIdentifier(FindRepoRoot(), "_workingDirectory").Should().BeEmpty();
    ScanForIdentifier(FindRepoRoot(), "int RefCount").Should().BeEmpty();
}

[Fact]
public void WorkspaceSwitchHandlers_ShouldBeRegistered() { /* DI 断言 */ }
```

- [ ] **Step 2: 运行，确认失败**

```bash
dotnet build tests/spine/Seeing.Agent.Invariants.Tests
```

- [ ] **Step 3: 收口**

单参 `GetFullPath` 改 `[Obsolete(..., error: true)]`；修剩余调用点；`ExecutionWorldTests` 断言双参；`LocalExecutionWorldTests` 注释说明"启动期兜底"。

- [ ] **Step 4: 全量验证并提交**

```bash
dotnet build Seeing.Agent.slnx
dotnet vstest tests/spine/Seeing.Agent.Invariants.Tests/bin/Debug/net10.0/Seeing.Agent.Invariants.Tests.dll --TestCaseFilter:"FullyQualifiedName~PathResolutionInvariantTests"
git add -A
git commit -m "test: enforce cwd, root authority and no-reference-count invariants"
```

---

## Self-Review

**Spec coverage：**

| Spec 章节 | 任务 |
|---|---|
| §5.1 cwd 唯一计算点 / 进程开关 | Task 2 |
| §5.2 ToolInvocation + 目录端口 + 契约 | Task 1、2、5、7 |
| §5.3 传递链 | Task 2 |
| §5.4 ResolveAllowed + 工具 | Task 3、4 |
| §5.5 非执行链 | Task 6 |
| §5.6 授权根/门闸/附件 | Task 8 |
| §5.7 工作区派生状态 | Task 13 |
| §5.8 切换流程 | Task 14 |
| §5.9 协同目录 + 上下文 | Task 7、9 |
| §5.10 多根工作台 | Task 10、11、12 |
| §5.11 安全模型 | Task 5、8、9 |
| §5.12 守卫与测试 | Task 15 |

**Review Focus 覆盖：** 6 条分别落在 Task 2 / 8+11 / 11 / 9 / 5+8 / 7。
