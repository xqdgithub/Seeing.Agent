# 工作区首页与启动决策 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让有界面的宿主在能确定工作区时直接进入系统页、确定不了时先显示工作区首页，并让侧栏切换和浏览目录都走同一条可回滚的 `Apply` 路径。

**Architecture:** 启动决策是 Abstractions 里的纯数据 + Core 里的纯函数，宿主在 `Host.Build` 之前捕获原始启动目录并登记请求；`InitializeSeeingAsync` 消费决策，未决议时不读项目路径也不结算。首次选定和侧栏切换都走 Hosting 里的 `WorkspaceSwitcher`：拒绝未决审批 → 取消在途并等到空闲 → 抑制编排器自动触发 → 改根 → 重读项目配置 → 只 await 一轮显式重载 → 由参与者重启 Gateway / 渠道子进程 / 调度器。目录选择是 WebUI 进程内的服务加一个共用对话框。

**Tech Stack:** .NET 10、Blazor Server、AntDesign 2.0、xUnit + FluentAssertions + Moq、Photino（桌面外壳）、`Seeing.Files.AtomicFileWrite`。

## Global Constraints

- 目标框架 `net10.0`，包版本走中央管理（`Directory.Packages.props`）。
- 依赖只能向下：能力包只引用 Abstractions（+ 原语 / support）；Core 不得引用 Gateway、Scheduler、Skills、Mcp。
- 不得新增扁平放在 `src/` 下的项目。
- 持久化写入一律走 `Seeing.Files.AtomicFileWrite`；读-改-写整段持有 `AtomicFileWrite.GetLock`。禁止自建 `temp + Move`。
- 「是否在某个根内」的判定一律走 `PathBoundary`，禁止内联前缀比较。
- 配置只从 `~/.seeing/seeing.json` 与 `./.seeing/seeing.json` 读取，不写 `appsettings.json`。
- 测试禁止固定时长等待（`Task.Delay` / `Thread.Sleep` 不得用于「等状态就绪」）；用条件轮询、`TaskCompletionSource` 或 `WaitUntilAsync`。
- 测试运行方式（MTP 无法发现测试）：
  ```bash
  dotnet build tests/spine/Seeing.Agent.Tests
  dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~WorkspaceLaunchPolicyTests"
  ```
- 每个任务结束都提交一次。

---

## 文件结构

| 文件 | 职责 |
|------|------|
| `src/abstractions/Seeing.Agent.Abstractions/Configuration/WorkspaceLaunch.cs` | 决策输入 / 结果 / 历史快照的纯数据类型 |
| `src/abstractions/Seeing.Agent.Abstractions/Configuration/IWorkspaceHistoryStore.cs` | 最近工作区历史的读写契约 |
| `src/abstractions/Seeing.Agent.Abstractions/Configuration/IWorkspaceApplyParticipant.cs` | 切换工作区时由能力包挂接的启停钩子 |
| `src/abstractions/Seeing.Agent.Abstractions/Execution/IExecutionInFlightBoundary.cs` | 已有；增加「等到空闲」 |
| `src/spine/Seeing.Agent.Core/Configuration/WorkspaceLaunchPolicy.cs` | 纯函数决议矩阵 |
| `src/spine/Seeing.Agent.Core/Configuration/WorkspaceHistoryStore.cs` | `~/.seeing/workspace_history.json` 的合并式读写 |
| `src/spine/Seeing.Agent.Core/Configuration/WorkspaceProvider.cs` | 未决议状态 + 项目路径守卫 |
| `src/spine/Seeing.Agent.Core/Configuration/ReloadOrchestrator.cs` | 增加「手动重载抑制」 |
| `src/spine/Seeing.Agent.Core/Extensions/ServiceCollectionExtensions.cs` | `InitializeSeeingAsync` 消费决策、门控项目装载 |
| `src/hosting/Seeing.Agent.Hosting/Execution/WorkspaceSwitcher.cs` | 唯一的 `Apply` 实现 |
| `src/gateway/Seeing.Agent.Gateway/Hosting/GatewayWorkspaceParticipant.cs` | 切换时停/起 Gateway 与渠道子进程 |
| `src/capabilities/Seeing.Agent.Scheduler/Configuration/SchedulerWorkspaceParticipant.cs` | 切换时停/起调度器 |
| `samples/Seeing.Agent.WebUI/Services/DirectoryBrowser.cs` | 进程内目录浏览 + 新建文件夹 |
| `samples/Seeing.Agent.WebUI/Components/DirectoryPicker.razor` | 共用的目录选择对话框 |
| `samples/Seeing.Agent.WebUI/Pages/WorkspacesPage.razor` | 工作区首页（`/workspaces`） |
| `samples/Seeing.Agent.WebUI/Components/WorkspaceSwitcherButton.razor` | 侧栏入口 |
| `samples/Seeing.Agent.WebUI/Hosting/WebUiHost.cs` | 登记启动请求、注册新服务、未决议时路由守卫 |

---

### Task 1: 启动决策的数据契约

**Files:**
- Create: `src/abstractions/Seeing.Agent.Abstractions/Configuration/WorkspaceLaunch.cs`
- Create: `src/abstractions/Seeing.Agent.Abstractions/Configuration/IWorkspaceHistoryStore.cs`

**Interfaces:**
- Consumes: 已有的 `WorkspaceResolutionSource`（`Seeing.Agent.Configuration` 命名空间，位于 Abstractions 程序集）
- Produces:
  - `enum WorkspaceLaunchMode { Invocation, Interactive, Service }`
  - `record WorkspaceHistorySnapshot(IReadOnlyList<string> Recent, string? LastInteractive)`
  - `record WorkspaceLaunchInput(WorkspaceLaunchMode Mode, string StartupDirectory, string? ExecutableDirectory, string? ExplicitPath, string? InheritedEnvironment, WorkspaceHistorySnapshot History)`
  - `record WorkspaceLaunchRequest(WorkspaceLaunchInput Input)` — 宿主在 `Build` 之前登记
  - `enum WorkspaceLaunchOutcome { Resolved, Unresolved, Failed }`
  - `record WorkspaceLaunchDecision(WorkspaceLaunchOutcome Outcome, string? WorkspaceRoot, WorkspaceResolutionSource Source, string? Error, bool UpdateRecent, bool UpdateLastInteractive)`
  - `interface IWorkspaceHistoryStore { WorkspaceHistorySnapshot Read(); void RecordOpened(string path, bool asDefault); }`

- [ ] **Step 1: 写类型文件**

```csharp
// src/abstractions/Seeing.Agent.Abstractions/Configuration/WorkspaceLaunch.cs
namespace Seeing.Agent.Configuration;

/// <summary>宿主启动方式：决定工作区从哪来。</summary>
public enum WorkspaceLaunchMode
{
    /// <summary>CLI / TUI：工作区就是本次启动目录（或 TUI 的位置参数）。</summary>
    Invocation = 0,

    /// <summary>WebUI / Desktop：可能进入工作区首页。</summary>
    Interactive = 1,

    /// <summary>单独的 Gateway.Server：没有首页。</summary>
    Service = 2,
}

/// <summary>最近工作区快照。不存在的目录由策略层过滤。</summary>
public sealed record WorkspaceHistorySnapshot(IReadOnlyList<string> Recent, string? LastInteractive)
{
    public static WorkspaceHistorySnapshot Empty { get; } =
        new(Array.Empty<string>(), null);
}

/// <summary>启动决议的输入。全部由宿主在 <c>Host.Build</c> 之前填好。</summary>
public sealed record WorkspaceLaunchInput(
    WorkspaceLaunchMode Mode,
    string StartupDirectory,
    string? ExecutableDirectory,
    string? ExplicitPath,
    string? InheritedEnvironment,
    WorkspaceHistorySnapshot History);

/// <summary>宿主登记的启动请求（单例）。缺失时宿主按 Interactive + 当前目录处理。</summary>
public sealed record WorkspaceLaunchRequest(WorkspaceLaunchInput Input);

/// <summary>决议结果类型。</summary>
public enum WorkspaceLaunchOutcome
{
    /// <summary>已确定工作区。</summary>
    Resolved = 0,

    /// <summary>有界面宿主且没有可用目录：显示工作区首页。</summary>
    Unresolved = 1,

    /// <summary>显式指定的目录无效：进程应启动失败。</summary>
    Failed = 2,
}

/// <summary>决议结果。<see cref="UpdateRecent"/> / <see cref="UpdateLastInteractive"/> 决定历史怎么写。</summary>
public sealed record WorkspaceLaunchDecision(
    WorkspaceLaunchOutcome Outcome,
    string? WorkspaceRoot,
    WorkspaceResolutionSource Source,
    string? Error,
    bool UpdateRecent,
    bool UpdateLastInteractive)
{
    public static WorkspaceLaunchDecision Failed(string error) =>
        new(WorkspaceLaunchOutcome.Failed, null, WorkspaceResolutionSource.StartupDirectory, error, false, false);

    public static WorkspaceLaunchDecision Unresolved() =>
        new(WorkspaceLaunchOutcome.Unresolved, null, WorkspaceResolutionSource.StartupDirectory, null, false, false);
}
```

```csharp
// src/abstractions/Seeing.Agent.Abstractions/Configuration/IWorkspaceHistoryStore.cs
namespace Seeing.Agent.Configuration;

/// <summary>
/// 最近工作区历史。读-改-写跨进程加锁，<c>recent</c> 按路径合并。
/// </summary>
public interface IWorkspaceHistoryStore
{
    /// <summary>当前快照。文件损坏或不存在时返回空快照，不抛异常。</summary>
    WorkspaceHistorySnapshot Read();

    /// <summary>
    /// 记录一次打开。<paramref name="asDefault"/> 为真时同时更新 <c>lastInteractive</c>。
    /// 写入失败只记日志，不抛出。
    /// </summary>
    void RecordOpened(string path, bool asDefault);
}
```

- [ ] **Step 2: 构建 Abstractions**

```bash
dotnet build src/abstractions/Seeing.Agent.Abstractions
```
Expected: 0 error。

- [ ] **Step 3: 提交**

```bash
git add src/abstractions/Seeing.Agent.Abstractions/Configuration/WorkspaceLaunch.cs src/abstractions/Seeing.Agent.Abstractions/Configuration/IWorkspaceHistoryStore.cs
git commit -m "feat(abstractions): 工作区启动决策与历史契约"
```

---

### Task 2: 决议矩阵（纯函数）

**Files:**
- Create: `src/spine/Seeing.Agent.Core/Configuration/WorkspaceLaunchPolicy.cs`
- Test: `tests/spine/Seeing.Agent.Tests/Configuration/WorkspaceLaunchPolicyTests.cs`

**Interfaces:**
- Consumes: Task 1 的 `WorkspaceLaunchInput`、`WorkspaceLaunchDecision`、`WorkspaceHistorySnapshot`
- Produces: `static WorkspaceLaunchDecision WorkspaceLaunchPolicy.Resolve(WorkspaceLaunchInput input, Func<string, bool> directoryExists)`

- [ ] **Step 1: 写失败测试**

```csharp
// tests/spine/Seeing.Agent.Tests/Configuration/WorkspaceLaunchPolicyTests.cs
using FluentAssertions;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Configuration;
using Xunit;

namespace Seeing.Agent.Tests.Configuration;

public class WorkspaceLaunchPolicyTests
{
    private const string Exe = @"C:\Program Files\Seeing";

    private static WorkspaceLaunchInput Input(
        WorkspaceLaunchMode mode,
        string startup,
        IReadOnlyList<string>? recent = null,
        string? lastInteractive = null,
        string? explicitPath = null,
        string? inherited = null,
        string? exe = Exe)
        => new(mode, startup, exe, explicitPath, inherited,
            new WorkspaceHistorySnapshot(recent ?? Array.Empty<string>(), lastInteractive));

    private static Func<string, bool> Existing(params string[] paths)
    {
        var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        return p => set.Contains(Path.TrimEndingDirectorySeparator(p));
    }

    [Fact]
    public void Invocation_ShouldUseStartupDirectory_AndIgnoreInheritedEnvironment()
    {
        var decision = WorkspaceLaunchPolicy.Resolve(
            Input(WorkspaceLaunchMode.Invocation, @"D:\tool\a",
                  inherited: @"E:\stale"),
            Existing(@"D:\tool\a", @"E:\stale"));

        decision.Outcome.Should().Be(WorkspaceLaunchOutcome.Resolved);
        decision.WorkspaceRoot.Should().Be(@"D:\tool\a");
        decision.Source.Should().Be(WorkspaceResolutionSource.StartupDirectory);
        decision.UpdateRecent.Should().BeTrue();
        decision.UpdateLastInteractive.Should().BeFalse();
    }

    [Fact]
    public void Invocation_TuiExplicitPath_ShouldWinOverStartupDirectory()
    {
        var decision = WorkspaceLaunchPolicy.Resolve(
            Input(WorkspaceLaunchMode.Invocation, @"D:\tool\a", explicitPath: @"D:\tool\b"),
            Existing(@"D:\tool\a", @"D:\tool\b"));

        decision.WorkspaceRoot.Should().Be(@"D:\tool\b");
    }

    [Fact]
    public void Invocation_MissingStartupDirectory_ShouldFail()
    {
        var decision = WorkspaceLaunchPolicy.Resolve(
            Input(WorkspaceLaunchMode.Invocation, @"D:\gone"), Existing());

        decision.Outcome.Should().Be(WorkspaceLaunchOutcome.Failed);
        decision.Error.Should().Contain(@"D:\gone");
    }

    [Fact]
    public void Interactive_ExplicitPath_ShouldBeatEnvironmentAndHistory()
    {
        var decision = WorkspaceLaunchPolicy.Resolve(
            Input(WorkspaceLaunchMode.Interactive, @"D:\cwd",
                  recent: [@"D:\hist"], lastInteractive: @"D:\hist",
                  explicitPath: @"D:\explicit", inherited: @"D:\env"),
            Existing(@"D:\cwd", @"D:\hist", @"D:\explicit", @"D:\env"));

        decision.WorkspaceRoot.Should().Be(@"D:\explicit");
        decision.UpdateLastInteractive.Should().BeFalse();
    }

    [Fact]
    public void Interactive_MissingExplicitPath_ShouldFailWithoutFallingBack()
    {
        var decision = WorkspaceLaunchPolicy.Resolve(
            Input(WorkspaceLaunchMode.Interactive, @"D:\cwd", lastInteractive: @"D:\cwd",
                  explicitPath: @"D:\gone"),
            Existing(@"D:\cwd"));

        decision.Outcome.Should().Be(WorkspaceLaunchOutcome.Failed);
        decision.WorkspaceRoot.Should().BeNull();
    }

    [Fact]
    public void Interactive_MissingInheritedEnvironment_ShouldFail()
    {
        var decision = WorkspaceLaunchPolicy.Resolve(
            Input(WorkspaceLaunchMode.Interactive, @"D:\cwd", lastInteractive: @"D:\cwd",
                  inherited: @"D:\gone"),
            Existing(@"D:\cwd"));

        decision.Outcome.Should().Be(WorkspaceLaunchOutcome.Failed);
    }

    [Fact]
    public void Interactive_StartupDirectoryInHistory_ShouldWin_WithoutTouchingLastInteractive()
    {
        var decision = WorkspaceLaunchPolicy.Resolve(
            Input(WorkspaceLaunchMode.Interactive, @"D:\cwd",
                  recent: [@"D:\other", @"D:\cwd"], lastInteractive: @"D:\other"),
            Existing(@"D:\cwd", @"D:\other"));

        decision.WorkspaceRoot.Should().Be(@"D:\cwd");
        decision.UpdateRecent.Should().BeFalse("已经在最近列表里，不必重写");
        decision.UpdateLastInteractive.Should().BeFalse();
    }

    [Fact]
    public void Interactive_StartupDirectoryEqualsExe_ShouldSkipHistoryHit()
    {
        var decision = WorkspaceLaunchPolicy.Resolve(
            Input(WorkspaceLaunchMode.Interactive, Exe, recent: [Exe], lastInteractive: @"D:\last"),
            Existing(Exe, @"D:\last"));

        decision.WorkspaceRoot.Should().Be(@"D:\last");
    }

    [Fact]
    public void Interactive_FallsBackToLastInteractive()
    {
        var decision = WorkspaceLaunchPolicy.Resolve(
            Input(WorkspaceLaunchMode.Interactive, @"D:\unrelated",
                  recent: [@"D:\hist"], lastInteractive: @"D:\hist"),
            Existing(@"D:\unrelated", @"D:\hist"));

        decision.WorkspaceRoot.Should().Be(@"D:\hist");
        decision.UpdateRecent.Should().BeFalse();
        decision.UpdateLastInteractive.Should().BeFalse();
    }

    [Fact]
    public void Interactive_MissingLastInteractive_ShouldBeUnresolved()
    {
        var decision = WorkspaceLaunchPolicy.Resolve(
            Input(WorkspaceLaunchMode.Interactive, @"D:\unrelated",
                  recent: [@"D:\gone"], lastInteractive: @"D:\gone"),
            Existing(@"D:\unrelated"));

        decision.Outcome.Should().Be(WorkspaceLaunchOutcome.Unresolved);
        decision.WorkspaceRoot.Should().BeNull();
    }

    [Fact]
    public void Service_ShouldFallBackToStartupDirectory_AndOnlyUpdateRecent()
    {
        var decision = WorkspaceLaunchPolicy.Resolve(
            Input(WorkspaceLaunchMode.Service, @"E:\svc", recent: [@"D:\hist"], lastInteractive: @"D:\hist"),
            Existing(@"E:\svc", @"D:\hist"));

        decision.WorkspaceRoot.Should().Be(@"E:\svc");
        decision.UpdateRecent.Should().BeTrue();
        decision.UpdateLastInteractive.Should().BeFalse();
    }

    [Fact]
    public void Service_MissingStartupDirectory_ShouldFail()
    {
        var decision = WorkspaceLaunchPolicy.Resolve(
            Input(WorkspaceLaunchMode.Service, @"E:\gone", lastInteractive: @"D:\hist"),
            Existing(@"D:\hist"));

        decision.Outcome.Should().Be(WorkspaceLaunchOutcome.Failed);
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

```bash
dotnet build tests/spine/Seeing.Agent.Tests
```
Expected: 编译错误 `WorkspaceLaunchPolicy` 不存在（这是预期的失败形态）。

- [ ] **Step 3: 写实现**

```csharp
// src/spine/Seeing.Agent.Core/Configuration/WorkspaceLaunchPolicy.cs
using Seeing.Agent.Configuration;

namespace Seeing.Agent.Core.Configuration;

/// <summary>
/// 启动决议矩阵（纯函数）。宿主把输入填好，结果决定工作区与历史写法。
/// <para>顺序与语义见 docs/superpowers/specs/2026-11-07-workspace-home-design.md §3。</para>
/// </summary>
public static class WorkspaceLaunchPolicy
{
    public static WorkspaceLaunchDecision Resolve(
        WorkspaceLaunchInput input,
        Func<string, bool> directoryExists)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(directoryExists);

        return input.Mode switch
        {
            WorkspaceLaunchMode.Invocation => ResolveInvocation(input, directoryExists),
            WorkspaceLaunchMode.Service => ResolveService(input, directoryExists),
            _ => ResolveInteractive(input, directoryExists),
        };
    }

    private static WorkspaceLaunchDecision ResolveInvocation(
        WorkspaceLaunchInput input,
        Func<string, bool> exists)
    {
        // TUI 的位置参数是用户本次显式指定；没有它才用启动目录。
        // 残留的 SEEING_WORKSPACE_ROOT 不参与：CLI 每次都可能在不同目录。
        var target = FirstNonEmpty(input.ExplicitPath, input.StartupDirectory);
        if (target is null || !exists(target))
            return WorkspaceLaunchDecision.Failed($"工作区目录不存在: {target ?? "(空)"}");

        return new WorkspaceLaunchDecision(
            WorkspaceLaunchOutcome.Resolved, Normalize(target),
            WorkspaceResolutionSource.StartupDirectory, null,
            UpdateRecent: true, UpdateLastInteractive: false);
    }

    private static WorkspaceLaunchDecision ResolveService(
        WorkspaceLaunchInput input,
        Func<string, bool> exists)
    {
        var target = FirstNonEmpty(input.ExplicitPath, input.InheritedEnvironment, input.StartupDirectory);
        if (target is null || !exists(target))
            return WorkspaceLaunchDecision.Failed($"工作区目录不存在: {target ?? "(空)"}");

        var source = FirstNonEmpty(input.ExplicitPath) is not null
            ? WorkspaceResolutionSource.ManualSwitch
            : FirstNonEmpty(input.InheritedEnvironment) is not null
                ? WorkspaceResolutionSource.EnvironmentVariable
                : WorkspaceResolutionSource.StartupDirectory;

        return new WorkspaceLaunchDecision(
            WorkspaceLaunchOutcome.Resolved, Normalize(target), source, null,
            UpdateRecent: true, UpdateLastInteractive: false);
    }

    private static WorkspaceLaunchDecision ResolveInteractive(
        WorkspaceLaunchInput input,
        Func<string, bool> exists)
    {
        // 1. --workspace：目录无效直接失败，不回退。
        var explicitPath = FirstNonEmpty(input.ExplicitPath);
        if (explicitPath is not null)
        {
            if (!exists(explicitPath))
                return WorkspaceLaunchDecision.Failed($"工作区目录不存在: {explicitPath}");

            return new WorkspaceLaunchDecision(
                WorkspaceLaunchOutcome.Resolved, Normalize(explicitPath),
                WorkspaceResolutionSource.ManualSwitch, null,
                UpdateRecent: true, UpdateLastInteractive: false);
        }

        // 2. 父进程交给这一个子进程的环境变量：同样不回退。
        var fromEnvironment = FirstNonEmpty(input.InheritedEnvironment);
        if (fromEnvironment is not null)
        {
            if (!exists(fromEnvironment))
                return WorkspaceLaunchDecision.Failed($"工作区目录不存在: {fromEnvironment}");

            return new WorkspaceLaunchDecision(
                WorkspaceLaunchOutcome.Resolved, Normalize(fromEnvironment),
                WorkspaceResolutionSource.EnvironmentVariable, null,
                UpdateRecent: true, UpdateLastInteractive: false);
        }

        // 3. 从某个打开过的目录启动：直接进入它。
        //    exe 目录一律不算（双击时 cwd 就是 exe 目录）。
        var startup = FirstNonEmpty(input.StartupDirectory);
        if (startup is not null
            && exists(startup)
            && !IsSameDirectory(startup, input.ExecutableDirectory)
            && input.History.Recent.Any(p => IsSameDirectory(p, startup)))
        {
            return new WorkspaceLaunchDecision(
                WorkspaceLaunchOutcome.Resolved, Normalize(startup),
                WorkspaceResolutionSource.StartupDirectory, null,
                UpdateRecent: false, UpdateLastInteractive: false);
        }

        // 4. 上次在界面里选定的目录。
        var last = FirstNonEmpty(input.History.LastInteractive);
        if (last is not null && exists(last))
        {
            return new WorkspaceLaunchDecision(
                WorkspaceLaunchOutcome.Resolved, Normalize(last),
                WorkspaceResolutionSource.GlobalDefault, null,
                UpdateRecent: false, UpdateLastInteractive: false);
        }

        // 5. 都没有：首页。
        return WorkspaceLaunchDecision.Unresolved();
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }
        return null;
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsSameDirectory(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            PathBoundary.PathComparison);
    }
}
```

`PathBoundary` 需要 `using Seeing.Agent.Abstractions.Paths;`（命名空间以实际文件为准，确认后补上）。

- [ ] **Step 4: 运行测试确认通过**

```bash
dotnet build tests/spine/Seeing.Agent.Tests
dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~WorkspaceLaunchPolicyTests"
```
Expected: 12 passed。

- [ ] **Step 5: 提交**

```bash
git add src/spine/Seeing.Agent.Core/Configuration/WorkspaceLaunchPolicy.cs tests/spine/Seeing.Agent.Tests/Configuration/WorkspaceLaunchPolicyTests.cs
git commit -m "feat(core): 工作区启动决议矩阵"
```

---

### Task 3: 历史存储（跨进程合并）

**Files:**
- Create: `src/spine/Seeing.Agent.Core/Configuration/WorkspaceHistoryStore.cs`
- Modify: `src/spine/Seeing.Agent.Core/Seeing.Agent.Core.csproj`（增加 `Seeing.Files` 项目引用）
- Test: `tests/spine/Seeing.Agent.Tests/Configuration/WorkspaceHistoryStoreTests.cs`

**Interfaces:**
- Consumes: `IWorkspaceHistoryStore`、`WorkspaceHistorySnapshot`（Task 1）
- Produces: `sealed class WorkspaceHistoryStore : IWorkspaceHistoryStore`，构造函数 `WorkspaceHistoryStore(string historyFilePath, ILogger<WorkspaceHistoryStore>? logger = null, Func<string, bool>? directoryExists = null)`

- [ ] **Step 1: 写失败测试**

```csharp
// tests/spine/Seeing.Agent.Tests/Configuration/WorkspaceHistoryStoreTests.cs
using FluentAssertions;
using Seeing.Agent.Core.Configuration;
using Xunit;

namespace Seeing.Agent.Tests.Configuration;

public class WorkspaceHistoryStoreTests
{
    [Fact]
    public void RecordOpened_ShouldPutPathFirst_AndUpdateLastInteractive()
    {
        using var dir = new HistoryDir();
        var store = dir.CreateStore();

        store.RecordOpened(@"D:\a", asDefault: false);
        store.RecordOpened(@"D:\b", asDefault: true);

        var snapshot = store.Read();
        snapshot.Recent.Should().Equal(@"D:\b", @"D:\a");
        snapshot.LastInteractive.Should().Be(@"D:\b");
    }

    [Fact]
    public void RecordOpened_WithoutDefault_ShouldKeepPreviousLastInteractive()
    {
        using var dir = new HistoryDir();
        var store = dir.CreateStore();

        store.RecordOpened(@"D:\a", asDefault: true);
        store.RecordOpened(@"D:\b", asDefault: false);

        store.Read().LastInteractive.Should().Be(@"D:\a");
    }

    [Fact]
    public void RecordOpened_ShouldDeduplicate_AndMoveToFront()
    {
        using var dir = new HistoryDir();
        var store = dir.CreateStore();

        store.RecordOpened(@"D:\a", false);
        store.RecordOpened(@"D:\b", false);
        store.RecordOpened(@"D:\a", false);

        store.Read().Recent.Should().Equal(@"D:\a", @"D:\b");
    }

    [Fact]
    public void Read_LegacyStringArray_ShouldBecomeRecent_AndFirstExistingBecomesDefault()
    {
        using var dir = new HistoryDir();
        dir.WriteRaw("""["D:\\gone","D:\\b","D:\\a"]""");

        var snapshot = dir.CreateStore().Read();

        snapshot.Recent.Should().Equal(@"D:\gone", @"D:\b", @"D:\a");
        snapshot.LastInteractive.Should().Be(@"D:\b", "第一条仍存在的路径当作双击默认目录");
    }

    [Fact]
    public void Read_CorruptJson_ShouldReturnEmpty()
    {
        using var dir = new HistoryDir();
        dir.WriteRaw("{ not json");

        dir.CreateStore().Read().Should().BeEquivalentTo(WorkspaceHistorySnapshot.Empty);
    }

    [Fact]
    public void RecordOpened_ShouldDropMissingPaths_AndCapAtThirty()
    {
        using var dir = new HistoryDir();
        var store = dir.CreateStore();
        for (var i = 0; i < 35; i++)
            store.RecordOpened($@"D:\w{i}", false);

        var snapshot = store.Read();
        snapshot.Recent.Should().HaveCount(30);
        snapshot.Recent[0].Should().Be(@"D:\w34");
    }

    [Fact]
    public void TwoStores_ShouldMergeRecent_InsteadOfOverwriting()
    {
        using var dir = new HistoryDir();
        var first = dir.CreateStore();
        var second = dir.CreateStore();

        // 都先读一遍，模拟两个实例各自持有旧快照
        first.Read();
        second.Read();

        first.RecordOpened(@"D:\a", true);
        second.RecordOpened(@"D:\b", false);

        var merged = first.Read();
        merged.Recent.Should().Contain(@"D:\a");
        merged.Recent.Should().Contain(@"D:\b");
        merged.LastInteractive.Should().Be(@"D:\a", "只有 asDefault 的写入改默认目录");
    }

    private sealed class HistoryDir : IDisposable
    {
        private readonly string _root =
            Path.Combine(Path.GetTempPath(), "seeing-history-" + Guid.NewGuid().ToString("N"));

        public HistoryDir() => Directory.CreateDirectory(_root);

        private string FilePath => Path.Combine(_root, "workspace_history.json");

        public WorkspaceHistoryStore CreateStore() =>
            new(FilePath, logger: null, directoryExists: p => Directory.Exists(p));

        public void WriteRaw(string content) => File.WriteAllText(FilePath, content);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                    Directory.Delete(_root, true);
            }
            catch (IOException)
            {
            }
        }
    }
}
```

- [ ] **Step 2: 运行确认失败**

```bash
dotnet build tests/spine/Seeing.Agent.Tests
```
Expected: `WorkspaceHistoryStore` 不存在。

- [ ] **Step 3: 增加 Seeing.Files 引用**

在 `src/spine/Seeing.Agent.Core/Seeing.Agent.Core.csproj` 的 `ItemGroup` 里、`Seeing.Session` 之前插入：

```xml
    <ProjectReference Include="..\..\primitives\Seeing.Files\Seeing.Files.csproj" />
```

- [ ] **Step 4: 写实现**

```csharp
// src/spine/Seeing.Agent.Core/Configuration/WorkspaceHistoryStore.cs
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Configuration;
using Seeing.Files;

namespace Seeing.Agent.Core.Configuration;

/// <summary>
/// 最近工作区历史。跨进程用命名互斥量串行化，进程内再持有 <see cref="AtomicFileWrite.GetLock"/>。
/// <para>
/// 为什么 <c>recent</c> 要按路径合并：两个实例各自读到同一份旧快照时，
/// 后写者整文件覆盖会让先写者刚加入的目录凭空消失。合并只对 <c>recent</c> 生效；
/// <c>lastInteractive</c> 是「最后一次界面选择」，后写者胜出才符合语义。
/// </para>
/// </summary>
public sealed class WorkspaceHistoryStore : IWorkspaceHistoryStore
{
    private const int MaxRecent = 30;
    private const string MutexName = @"Local\Seeing.Agent.WorkspaceHistory";

    private readonly string _filePath;
    private readonly ILogger<WorkspaceHistoryStore>? _logger;
    private readonly Func<string, bool> _directoryExists;

    public WorkspaceHistoryStore(
        string historyFilePath,
        ILogger<WorkspaceHistoryStore>? logger = null,
        Func<string, bool>? directoryExists = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(historyFilePath);
        _filePath = historyFilePath;
        _logger = logger;
        _directoryExists = directoryExists ?? Directory.Exists;
    }

    public WorkspaceHistorySnapshot Read()
    {
        var data = Load();
        return new WorkspaceHistorySnapshot(data.Recent, data.LastInteractive);
    }

    public void RecordOpened(string path, bool asDefault)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        // 进程内锁必须先拿：同一进程的多个 circuit 会并发写。
        var gate = AtomicFileWrite.GetLock(_filePath);
        gate.Wait();
        try
        {
            using var mutex = AcquireCrossProcess();
            var data = Load();

            var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var recent = new List<string> { normalized };
            recent.AddRange(data.Recent.Where(p => !IsSame(p, normalized)));
            recent = recent.Take(MaxRecent).ToList();

            var lastInteractive = asDefault ? normalized : data.LastInteractive;

            Save(new HistoryFile
            {
                Recent = recent,
                LastInteractive = lastInteractive,
            });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "写入工作区历史失败: {Path}", path);
        }
        finally
        {
            gate.Release();
        }
    }

    private HistoryFile Load()
    {
        try
        {
            if (!File.Exists(_filePath))
                return new HistoryFile();

            var json = File.ReadAllText(_filePath);
            if (string.IsNullOrWhiteSpace(json))
                return new HistoryFile();

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                // 旧格式：字符串数组，最前为最新。
                var legacy = root.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .ToList();

                return new HistoryFile
                {
                    Recent = legacy,
                    LastInteractive = legacy.FirstOrDefault(_directoryExists),
                };
            }

            if (root.ValueKind == JsonValueKind.Object)
            {
                var parsed = JsonSerializer.Deserialize<HistoryFile>(json);
                if (parsed is null)
                    return new HistoryFile();

                return new HistoryFile
                {
                    Recent = parsed.Recent.Where(p => !string.IsNullOrWhiteSpace(p)).ToList(),
                    LastInteractive = parsed.LastInteractive,
                };
            }

            return new HistoryFile();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "读取工作区历史失败，按空历史处理: {Path}", _filePath);
            return new HistoryFile();
        }
    }

    private void Save(HistoryFile data)
    {
        // 不存在的目录不留：下次启动也不该尝试进入它们。
        data.Recent = data.Recent.Where(_directoryExists).ToList();
        if (data.LastInteractive is not null && !_directoryExists(data.LastInteractive))
            data.LastInteractive = null;

        var json = JsonSerializer.Serialize(data, SeeingJson.Readable);
        AtomicFileWrite.WriteAllTextAtomic(_filePath, json, "workspace_history", _logger);
    }

    private Mutex AcquireCrossProcess()
    {
        var mutex = new Mutex(initiallyOwned: false, MutexName);
        try
        {
            try
            {
                mutex.WaitOne(TimeSpan.FromSeconds(5));
            }
            catch (AbandonedMutexException)
            {
                // 上一个持有者崩溃：互斥量已归我们，继续即可。
            }
            return mutex;
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    private static bool IsSame(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            PathBoundary.PathComparison);

    private sealed class HistoryFile
    {
        [JsonPropertyName("recent")]
        public List<string> Recent { get; set; } = new();

        [JsonPropertyName("lastInteractive")]
        public string? LastInteractive { get; set; }
    }
}
```

说明：`AcquireCrossProcess` 返回的 `Mutex` 在 `using` 中释放时若未 `ReleaseMutex` 会抛 `ApplicationException`。改为在返回前标记，或用下面的写法替换 `AcquireCrossProcess` 的调用：

```csharp
            using var mutex = AcquireCrossProcess();
            try
            {
                // ... 读写 ...
            }
            finally
            {
                mutex.ReleaseMutex();
            }
```

- [ ] **Step 5: 运行测试确认通过**

```bash
dotnet build tests/spine/Seeing.Agent.Tests
dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~WorkspaceHistoryStoreTests"
```
Expected: 7 passed。

- [ ] **Step 6: 提交**

```bash
git add src/spine/Seeing.Agent.Core/Configuration/WorkspaceHistoryStore.cs src/spine/Seeing.Agent.Core/Seeing.Agent.Core.csproj tests/spine/Seeing.Agent.Tests/Configuration/WorkspaceHistoryStoreTests.cs
git commit -m "feat(core): 最近工作区历史存储（跨进程合并）"
```

---

### Task 4: WorkspaceProvider 的未决议状态

**Files:**
- Modify: `src/spine/Seeing.Agent.Core/Configuration/WorkspaceProvider.cs`
- Test: `tests/spine/Seeing.Agent.Tests/Configuration/WorkspaceProviderUnresolvedTests.cs`

**Interfaces:**
- Consumes: 现有 `IWorkspaceProvider`
- Produces:
  - `bool IWorkspaceProvider.IsResolved { get; }`（新增，进接口）
  - `string? IWorkspaceProvider.ResolvedRoot { get; }`（新增，进接口；已决议时为工作区根，否则 null）
  - `void WorkspaceProvider.SetInitialWorkspace(string path, WorkspaceResolutionSource source)`（新增）
  - `InitializeAsync` 在未调用 `SetInitialWorkspace` 时不再自行决议，直接返回
  - `ProjectSeeingDirectory` / `GetProjectRoot()` 在未决议时抛 `InvalidOperationException`

> `ResolvedRoot` 必须进接口：`GetProjectRoot()` 是扩展方法，Moq 无法替身化；
> 切换流程与测试都需要一个可直接读取的根。

- [ ] **Step 1: 写失败测试**

```csharp
// tests/spine/Seeing.Agent.Tests/Configuration/WorkspaceProviderUnresolvedTests.cs
using FluentAssertions;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Configuration;
using Xunit;

namespace Seeing.Agent.Tests.Configuration;

public class WorkspaceProviderUnresolvedTests
{
    [Fact]
    public void NewProvider_ShouldNotExposeStartupDirectoryAsWorkspace()
    {
        var provider = new WorkspaceProvider();

        provider.IsResolved.Should().BeFalse();
        provider.Invoking(p => p.ProjectSeeingDirectory)
            .Should().Throw<InvalidOperationException>();
        provider.Invoking(p => p.GetProjectRoot())
            .Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task InitializeAsync_WithoutInitialWorkspace_ShouldNotResolve()
    {
        var provider = new WorkspaceProvider();

        await provider.InitializeAsync(TestContext.Current.CancellationToken);

        provider.IsResolved.Should().BeFalse();
    }

    [Fact]
    public void SetInitialWorkspace_ShouldResolveAndExposeProjectRoot()
    {
        using var dir = new TempDir();
        var provider = new WorkspaceProvider();

        provider.SetInitialWorkspace(dir.Root, WorkspaceResolutionSource.StartupDirectory);

        provider.IsResolved.Should().BeTrue();
        provider.ResolvedRoot.Should().Be(dir.Root);
        provider.ProjectSeeingDirectory.Should().Be(Path.Combine(dir.Root, ".seeing"));
        provider.GetProjectRoot().Should().Be(dir.Root);
    }

    [Fact]
    public void ExplicitConstructor_ShouldStillResolve()
    {
        using var dir = new TempDir();
        var provider = new WorkspaceProvider(dir.Root);

        provider.IsResolved.Should().BeTrue();
        provider.GetProjectRoot().Should().Be(dir.Root);
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Root = Path.Combine(Path.GetTempPath(), "seeing-ws-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, true);
            }
            catch (IOException)
            {
            }
        }
    }
}
```

- [ ] **Step 2: 运行确认失败**

```bash
dotnet build tests/spine/Seeing.Agent.Tests
```
Expected: `IsResolved` / `SetInitialWorkspace` 不存在。

- [ ] **Step 3: 改 WorkspaceProvider**

字段与构造函数改为：

```csharp
    private volatile string? _workspaceRoot;
    private readonly string _startupDirectory;
    private string? _globalWorkspaceRoot;
    private WorkspaceResolutionSource _resolutionSource;
    private WorkspaceOptions _workspaceOptions = new();
    private readonly object _lock = new();

    // 依赖服务（延迟注入）
    private UnifiedConfigManager? _configManager;
    private ILogger<WorkspaceProvider>? _logger;

    /// <summary>
    /// 创建工作区路径提供者。
    /// <para>
    /// <b>未决议</b>：不把当前目录当作工作区根。项目路径在 <see cref="SetInitialWorkspace"/>
    /// 之前不可读，避免宿主在用户选定工作区之前把会话库、调度库钉在错误的目录上。
    /// </para>
    /// </summary>
    public WorkspaceProvider()
    {
        _startupDirectory = Directory.GetCurrentDirectory();
        _workspaceRoot = null;
        _resolutionSource = WorkspaceResolutionSource.StartupDirectory;
    }

    /// <summary>创建工作区路径提供者并立即决议（测试与 ChannelHost 使用）。</summary>
    /// <param name="workspaceRoot">工作区根目录</param>
    public WorkspaceProvider(string? workspaceRoot) : this()
    {
        if (string.IsNullOrEmpty(workspaceRoot) || !Directory.Exists(workspaceRoot))
            throw new DirectoryNotFoundException($"工作区目录不存在: {workspaceRoot}");

        _workspaceRoot = workspaceRoot;
    }
```

新增成员：

```csharp
    /// <inheritdoc/>
    public bool IsResolved => _workspaceRoot is not null;

    /// <inheritdoc/>
    public string? ResolvedRoot => _workspaceRoot;

    /// <summary>
    /// 按启动决议设定工作区根。只允许一次（进程内首次选定），之后用 <see cref="SetWorkspaceRoot"/>。
    /// </summary>
    public void SetInitialWorkspace(string workspaceRoot, WorkspaceResolutionSource source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        if (!Directory.Exists(workspaceRoot))
            throw new DirectoryNotFoundException($"工作区目录不存在: {workspaceRoot}");

        SetWorkspaceRootInternal(workspaceRoot, source);
    }

    /// <inheritdoc/>
    public string ProjectSeeingDirectory
    {
        get
        {
            var root = _workspaceRoot ?? throw new InvalidOperationException(
                "工作区尚未选定；宿主必须在 InitializeSeeingAsync 之前调用 SetInitialWorkspace，" +
                "或等待用户在首页选定目录。");
            return Path.Combine(root, ".seeing");
        }
    }
```

`InitializeAsync` 改为：

```csharp
    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        // 决议已由宿主按 WorkspaceLaunchPolicy 完成（见 docs/superpowers/specs/2026-11-07-workspace-home-design.md §3）。
        // 本方法不再读环境变量 / CustomPath / UseGlobal：那些来源已退出启动选择。
        if (!IsResolved)
        {
            _logger?.LogInformation("工作区未选定：等待用户在首页选择");
            return Task.CompletedTask;
        }

        _logger?.LogInformation("工作区来源: {Source}, 路径: {Path}", _resolutionSource, _workspaceRoot);
        return Task.CompletedTask;
    }
```

把 `InitializeAsync` 内原有的 6 段解析逻辑（环境变量 / 全局 / 项目 / 自定义 / UseGlobal / 启动目录）全部删除。`SetGlobalWorkspaceRootAsync` 与 `SetWorkspaceOptionsAsync` 保留，但 `SetWorkspaceOptionsAsync` 末尾的 `await InitializeAsync(...)` 删除，改为只保存选项：

```csharp
    /// <inheritdoc/>
    public async Task SetWorkspaceOptionsAsync(WorkspaceOptions options, CancellationToken cancellationToken = default)
    {
        if (_configManager == null)
            throw new InvalidOperationException("UnifiedConfigManager 未注入");

        await _configManager.SaveSectionAsync("Workspace", options, ConfigLevel.Project, cancellationToken);
        _workspaceOptions = options;
        _logger?.LogInformation("已保存项目工作区配置: RestrictToWorkspace={RestrictToWorkspace}",
            options.RestrictToWorkspace);
    }
```

`SetWorkspaceRootInternal` 的签名改为接受 `string?` 的旧根并保持不变，但 `WorkspaceRootChanged` 只在 `IsResolved` 时按 `PathBoundary.PathComparison` 比较：

```csharp
        if (!IsSameDirectory(oldWorkspace, path))
        {
            WorkspaceRootChanged?.Invoke(this, new WorkspaceChangedEventArgs
            {
                OldWorkspace = oldWorkspace ?? string.Empty,
                NewWorkspace = path
            });
        }

    private static bool IsSameDirectory(string? left, string? right) =>
        left is not null && right is not null &&
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            PathBoundary.PathComparison);
```

- [ ] **Step 4: 运行测试确认通过**

```bash
dotnet build tests/spine/Seeing.Agent.Tests
dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~WorkspaceProviderUnresolvedTests"
```
Expected: 4 passed。

- [ ] **Step 5: 修既有测试**

`tests/spine/Seeing.Agent.Tests/Configuration/WorkspaceSwitchConfigReloadTests.cs` 依赖 `UseGlobal` 决议，改为直接验证「决议由宿主给出后配置重载生效」：

```csharp
            var provider = sp.GetRequiredService<WorkspaceProvider>();
            provider.SetInitialWorkspace(globalWorkspace.Root, WorkspaceResolutionSource.GlobalDefault);
```

并在文件顶部删除 `Environment.SetEnvironmentVariable` 的用法。其余断言保留。

- [ ] **Step 6: 提交**

```bash
git add src/spine/Seeing.Agent.Core/Configuration/WorkspaceProvider.cs tests/spine/Seeing.Agent.Tests/Configuration/WorkspaceProviderUnresolvedTests.cs tests/spine/Seeing.Agent.Tests/Configuration/WorkspaceSwitchConfigReloadTests.cs
git commit -m "refactor(core): WorkspaceProvider 支持未决议状态并停止读取旧工作区来源"
```

---

### Task 5: 初始化流程门控

**Files:**
- Modify: `src/spine/Seeing.Agent.Core/Extensions/ServiceCollectionExtensions.cs:854-910`
- Test: `tests/spine/Seeing.Agent.Tests/Configuration/InitializeSeeingUnresolvedTests.cs`

**Interfaces:**
- Consumes: `WorkspaceLaunchRequest`（Task 1）、`WorkspaceLaunchPolicy`（Task 2）、`IWorkspaceHistoryStore`（Task 1）、`WorkspaceProvider.IsResolved`（Task 4）
- Produces: `InitializeSeeingAsync` 在未决议时只加载用户级配置并返回；`IWorkspaceLaunchState`（新增单例，暴露 `bool IsReady`）

- [ ] **Step 1: 新增启动状态契约**

```csharp
// src/abstractions/Seeing.Agent.Abstractions/Configuration/IWorkspaceLaunchState.cs
namespace Seeing.Agent.Configuration;

/// <summary>进程内「是否已有工作区」的只读事实，供托管服务决定能否启动。</summary>
public interface IWorkspaceLaunchState
{
    bool IsReady { get; }
}
```

Core 实现：

```csharp
// src/spine/Seeing.Agent.Core/Configuration/WorkspaceLaunchState.cs
using Seeing.Agent.Configuration;

namespace Seeing.Agent.Core.Configuration;

internal sealed class WorkspaceLaunchState : IWorkspaceLaunchState
{
    private volatile bool _ready;
    public bool IsReady => _ready;
    public void MarkReady() => _ready = true;
}
```

- [ ] **Step 2: 写失败测试**

```csharp
// tests/spine/Seeing.Agent.Tests/Configuration/InitializeSeeingUnresolvedTests.cs
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Extensions;
using Xunit;

namespace Seeing.Agent.Tests.Configuration;

public class InitializeSeeingUnresolvedTests
{
    [Fact]
    public async Task InitializeSeeing_WithoutLaunchRequest_ShouldStayUnresolved()
    {
        var services = new ServiceCollection();
        var registry = ConfigSectionRegistry.CreateWithSpine();
        services.AddSingleton<IConfigSectionRegistry>(registry);
        services.AddSingleton<ILoggerFactory>(LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning)));
        services.AddSeeingCore(registry);

        await using var sp = services.BuildServiceProvider();

        await sp.InitializeSeeingAsync(TestContext.Current.CancellationToken);

        sp.GetRequiredService<WorkspaceProvider>().IsResolved.Should().BeFalse(
            "没有决议就没有项目路径，业务服务不得被钉在启动目录上");
        sp.GetRequiredService<IWorkspaceLaunchState>().IsReady.Should().BeFalse();
    }
}
```

- [ ] **Step 3: 运行确认失败**

Expected: `IWorkspaceLaunchState` 不存在；断言 `IsResolved` 失败。

- [ ] **Step 4: 改 InitializeSeeingAsync**

把工作区段（原 862–880 行附近）替换为：

```csharp
            // 初始化工作区：决议已由宿主在 Build 之前登记（WorkspaceLaunchPolicy），
            // 这里只消费它。未决议时项目路径不可读，因此不结算、不 Activate、不 LoadAll。
            if (services.GetService<WorkspaceProvider>() is { } workspaceProvider)
            {
                configManager = services.GetService<UnifiedConfigManager>();
                var workspaceLogger = services.GetService<ILogger<WorkspaceProvider>>();

                if (configManager != null)
                {
                    // 用户级配置先加载：首页的最近列表与新工作区的 .seeing 都不依赖项目级文件
                    await configManager.LoadAsync(cancellationToken);
                    workspaceProvider.SetDependencies(configManager, workspaceLogger);

                    if (services.GetService<WorkspaceLaunchRequest>() is { } request)
                    {
                        var history = services.GetService<IWorkspaceHistoryStore>();
                        var decision = WorkspaceLaunchPolicy.Resolve(
                            request.Input with { History = history?.Read() ?? WorkspaceHistorySnapshot.Empty },
                            Directory.Exists);

                        if (decision.Outcome == WorkspaceLaunchOutcome.Failed)
                            throw new InvalidOperationException(decision.Error ?? "工作区决议失败");

                        if (decision.Outcome == WorkspaceLaunchOutcome.Resolved && decision.WorkspaceRoot is { } root)
                        {
                            workspaceProvider.SetInitialWorkspace(root, decision.Source);

                            // 项目级配置随工作区切换，必须重新加载
                            await configManager.LoadAsync(cancellationToken);
                        }
                    }
                    else
                    {
                        await workspaceProvider.InitializeAsync(cancellationToken);
                    }

                    if (workspaceProvider.IsResolved)
                    {
                        (services.GetService<WorkspaceLaunchState>() as WorkspaceLaunchState)?.MarkReady();
                    }
                }
            }
```

未决议时跳过结算与加载：

```csharp
            // 切根时清空白名单与权限记忆（须在工作区 Initialize 之后挂接，避免启动期误清）
            services.GetService<WorkspaceBoundaryLifecycle>()?.Attach();

            // 重载 Handler 延后挂载：须在模块 Activate（可能 Publish）之前，且不得在编排器 ctor 全量物化
            services.AttachReloadHandlers();

            var workspaceRoot = services.GetRequiredService<IWorkspaceProvider>();

            if (workspaceRoot.IsResolved)
            {
                await SettleAndActivateModulesAsync(services, configManager, logger, cancellationToken);

                var componentManager = services.GetRequiredService<IComponentManager>();
                var results = await componentManager.LoadAllAsync(
                    workspaceRoot.GetProjectRoot(), cancellationToken);

                var toolInvoker = services.GetService<ToolManager>();
                if (toolInvoker != null)
                    await toolInvoker.LoadToolStateAsync(cancellationToken);

                return results;
            }

            logger?.LogInformation("工作区未选定：跳过模块结算与组件加载，等待首页选择");
            return Array.Empty<ComponentLoadResult>();
```

`IWorkspaceProvider` 上的 `IsResolved` 需要加进接口（Task 4 只加了具体类成员）：

```csharp
    /// <summary>工作区是否已选定。未选定时 <see cref="ISeeingDirectories.ProjectSeeingDirectory"/> 抛异常。</summary>
    bool IsResolved { get; }
```

- [ ] **Step 5: 注册历史存储与状态**

在 `AddSeeingCore` 的 5. 段之后：

```csharp
            services.TryAddSingleton<IWorkspaceHistoryStore>(sp => new WorkspaceHistoryStore(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".seeing", "workspace_history.json"),
                sp.GetService<ILogger<WorkspaceHistoryStore>>()));
            services.TryAddSingleton<WorkspaceLaunchState>();
            services.TryAddSingleton<IWorkspaceLaunchState>(sp => sp.GetRequiredService<WorkspaceLaunchState>());
```

- [ ] **Step 6: 运行测试确认通过**

```bash
dotnet build tests/spine/Seeing.Agent.Tests
dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~InitializeSeeingUnresolvedTests"
```
Expected: 1 passed。

- [ ] **Step 7: 提交**

```bash
git add src/abstractions/Seeing.Agent.Abstractions/Configuration/IWorkspaceLaunchState.cs src/abstractions/Seeing.Agent.Abstractions/Configuration/IWorkspaceProvider.cs src/spine/Seeing.Agent.Core/Configuration/WorkspaceLaunchState.cs src/spine/Seeing.Agent.Core/Extensions/ServiceCollectionExtensions.cs tests/spine/Seeing.Agent.Tests/Configuration/InitializeSeeingUnresolvedTests.cs
git commit -m "feat(core): 未决议时跳过项目装载并登记启动状态"
```

---

### Task 6: 编排器的「手动重载」模式

**Files:**
- Modify: `src/spine/Seeing.Agent.Core/Configuration/ReloadOrchestrator.cs`
- Test: `tests/spine/Seeing.Agent.Tests/Configuration/ReloadOrchestratorSuppressionTests.cs`

**Interfaces:**
- Consumes: 现有 `ReloadOrchestrator.ReloadAsync`
- Produces: `IDisposable ReloadOrchestrator.SuppressAutoReload()` — 作用域内 `WorkspaceRootChanged` / `ConfigChanged` 只记录、不排队；调用方自行 `ReloadAsync`

- [ ] **Step 1: 写失败测试**

```csharp
// tests/spine/Seeing.Agent.Tests/Configuration/ReloadOrchestratorSuppressionTests.cs
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Configuration;
using Xunit;

namespace Seeing.Agent.Tests.Configuration;

public class ReloadOrchestratorSuppressionTests
{
    [Fact]
    public async Task SuppressAutoReload_ShouldBlockWorkspaceTriggeredReload()
    {
        var workspace = new Mock<IWorkspaceProvider>();
        var configStore = new Mock<IConfigSectionStore>();

        using var orchestrator = new ReloadOrchestrator(
            NullLogger<ReloadOrchestrator>.Instance,
            configStore.Object,
            workspace.Object,
            moduleReloadOptions: null);

        var handler = new CountingHandler();
        orchestrator.RegisterHandler(handler);

        using (orchestrator.SuppressAutoReload())
        {
            workspace.Raise(w => w.WorkspaceRootChanged += null,
                new WorkspaceChangedEventArgs { OldWorkspace = @"C:\a", NewWorkspace = @"C:\b" });

            // 抑制期间自动触发不得进入队列
            await Task.Yield();
        }

        handler.Calls.Should().Be(0);
    }

    private sealed class CountingHandler : IReloadHandler
    {
        public int Calls;
        public string ComponentId => "counting";
        public IReadOnlyList<Type> ChangeTypes { get; } = new[] { typeof(WorkspaceChange), typeof(ConfigChange) };

        public Task ReloadAsync(IReloadSignal change, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            return Task.CompletedTask;
        }
    }
}
```

- [ ] **Step 2: 运行确认失败**

Expected: `SuppressAutoReload` 不存在。

- [ ] **Step 3: 实现抑制**

在 `ReloadOrchestrator` 增加字段与成员：

```csharp
    private int _suppressDepth;

    /// <summary>
    /// 抑制自动重载作用域。切换工作区时由 <c>WorkspaceSwitcher</c> 使用：
    /// <c>SetWorkspaceRoot</c> 会触发 <c>WorkspaceRootChanged</c>，若同时再由调用方显式重载，
    /// 同一变更会跑两轮，模块生命周期与结算不是按可重入设计的。
    /// </summary>
    public IDisposable SuppressAutoReload()
    {
        Interlocked.Increment(ref _suppressDepth);
        return new SuppressScope(this);
    }

    private bool IsSuppressed => Volatile.Read(ref _suppressDepth) > 0;

    private sealed class SuppressScope : IDisposable
    {
        private ReloadOrchestrator? _owner;
        public SuppressScope(ReloadOrchestrator owner) => _owner = owner;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is not null)
                Interlocked.Decrement(ref owner._suppressDepth);
        }
    }
```

`OnConfigChanged` 与 `OnWorkspaceChanged` 开头加：

```csharp
        if (IsSuppressed)
        {
            _logger.LogDebug("自动重载已抑制（手动切换进行中），跳过本次触发");
            return;
        }
```

- [ ] **Step 4: 运行测试确认通过**

```bash
dotnet build tests/spine/Seeing.Agent.Tests
dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~ReloadOrchestratorSuppressionTests"
```
Expected: 1 passed。

- [ ] **Step 5: 提交**

```bash
git add src/spine/Seeing.Agent.Core/Configuration/ReloadOrchestrator.cs tests/spine/Seeing.Agent.Tests/Configuration/ReloadOrchestratorSuppressionTests.cs
git commit -m "feat(core): 重载编排器支持手动重载抑制"
```

---

### Task 7: 切换工作区的参与者契约

**Files:**
- Create: `src/abstractions/Seeing.Agent.Abstractions/Configuration/IWorkspaceApplyParticipant.cs`
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Execution/IExecutionInFlightBoundary.cs`

**Interfaces:**
- Produces:
  - `record WorkspaceApplyContext(string? OldRoot, string NewRoot)`
  - `interface IWorkspaceApplyParticipant { string ComponentId { get; } Task OnApplyingAsync(WorkspaceApplyContext ctx, CancellationToken ct); Task OnAppliedAsync(WorkspaceApplyContext ctx, CancellationToken ct); }`
  - `IExecutionInFlightBoundary.WaitUntilIdleAsync(CancellationToken)`（默认实现返回已完成 Task，兼容既有替身）

- [ ] **Step 1: 写契约**

```csharp
// src/abstractions/Seeing.Agent.Abstractions/Configuration/IWorkspaceApplyParticipant.cs
namespace Seeing.Agent.Configuration;

/// <summary>一次工作区切换的上下文。</summary>
public sealed record WorkspaceApplyContext(string? OldRoot, string NewRoot);

/// <summary>
/// 工作区切换的参与者：能力包在这里停掉依赖项目路径的运行时，
/// 并在根变更与重载完成后按新根重新拉起。
/// <para>顺序保证：所有 <see cref="OnApplyingAsync"/> 在改根之前跑完，
/// 所有 <see cref="OnAppliedAsync"/> 在一轮重载成功之后跑。</para>
/// </summary>
public interface IWorkspaceApplyParticipant
{
    string ComponentId { get; }

    /// <summary>改根之前：停掉跑在旧根上的东西。失败视为切换失败。</summary>
    Task OnApplyingAsync(WorkspaceApplyContext context, CancellationToken ct);

    /// <summary>重载成功后：按新根重新启动。失败记日志，不视为切换失败。</summary>
    Task OnAppliedAsync(WorkspaceApplyContext context, CancellationToken ct);
}
```

`IExecutionInFlightBoundary` 增加：

```csharp
    /// <summary>等到没有未终态执行为止。默认实现立即返回，供无执行引擎的替身使用。</summary>
    Task WaitUntilIdleAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
```

- [ ] **Step 2: 实现「等到空闲」**

在 `src/hosting/Seeing.Agent.Hosting/Execution/SessionRuntime.cs` 的 `IExecutionInFlightBoundary` 区域：

```csharp
    public async Task WaitUntilIdleAsync(CancellationToken cancellationToken = default)
    {
        // HasInFlight 读的是 CurrentExecution != null；取消信号发出后循环还要跑完收尾，
        // 期间改根会让收尾把结果写进新工作区的存储。必须等它真的落地。
        while (HasAnyActiveExecution())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }
```

（这里的 50ms 是轮询间隔，属允许的例外 E5。）

- [ ] **Step 3: 构建**

```bash
dotnet build src/hosting/Seeing.Agent.Hosting
```
Expected: 0 error。

- [ ] **Step 4: 提交**

```bash
git add src/abstractions/Seeing.Agent.Abstractions/Configuration/IWorkspaceApplyParticipant.cs src/abstractions/Seeing.Agent.Abstractions/Execution/IExecutionInFlightBoundary.cs src/hosting/Seeing.Agent.Hosting/Execution/SessionRuntime.cs
git commit -m "feat(abstractions): 工作区切换参与者契约与在途空闲等待"
```

---

### Task 8: WorkspaceSwitcher

**Files:**
- Create: `src/hosting/Seeing.Agent.Hosting/Execution/WorkspaceSwitcher.cs`
- Modify: `src/hosting/Seeing.Agent.Hosting/HostingServiceCollectionExtensions.cs`（或等价的 `AddHostShapeBaseline` 注册点）
- Test: `tests/hosting/Seeing.Agent.Hosting.Tests/Execution/WorkspaceSwitcherTests.cs`

**Interfaces:**
- Consumes: `IWorkspaceProvider`、`IWorkspaceHistoryStore`、`UnifiedConfigManager`、`ReloadOrchestrator`、`IExecutionInFlightBoundary`、`IPermissionRequestManager`、`IWorkspaceApplyParticipant`
- Produces:
  - `record WorkspaceApplyRequest(string NewRoot)` 
  - `record WorkspaceApplyResult(bool Success, string? Error, IReadOnlyList<ReloadResult> ReloadResults)`
  - `interface IWorkspaceSwitcher { Task<WorkspaceApplyResult> ApplyAsync(WorkspaceApplyRequest request, CancellationToken ct = default); }`
  - `sealed class WorkspaceSwitcher : IWorkspaceSwitcher`（Singleton）

- [ ] **Step 1: 写失败测试**

```csharp
// tests/hosting/Seeing.Agent.Hosting.Tests/Execution/WorkspaceSwitcherTests.cs
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Hosting.Execution;
using Xunit;

namespace Seeing.Agent.Hosting.Tests.Execution;

public class WorkspaceSwitcherTests
{
    [Fact]
    public async Task Apply_WithoutInFlight_ShouldSkipCancel()
    {
        var harness = new Harness(inFlight: false);

        var result = await harness.Switcher.ApplyAsync(
            new WorkspaceApplyRequest(harness.NewRoot),
            TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        harness.Runtime.Verify(r => r.CancelAllInFlightAsync(It.IsAny<CancellationToken>()), Times.Never);
        harness.Workspace.Verify(w => w.SetWorkspaceRoot(harness.NewRoot), Times.Once);
        harness.History.Verify(h => h.RecordOpened(harness.NewRoot, true), Times.Once);
        harness.Participant.Applied.Should().Be(1);
        harness.Participant.Applying.Should().Be(1);
        harness.Events.Should().Equal("applying", "reload", "applied");
    }

    [Fact]
    public async Task Apply_WithInFlight_ShouldRejectPermissions_ThenCancel_ThenWaitIdle()
    {
        var harness = new Harness(inFlight: true);

        await harness.Switcher.ApplyAsync(
            new WorkspaceApplyRequest(harness.NewRoot),
            TestContext.Current.CancellationToken);

        harness.Permissions.Verify(p => p.GetAllPending(), Times.Once);
        harness.Runtime.Verify(r => r.CancelAllInFlightAsync(It.IsAny<CancellationToken>()), Times.Once);
        harness.Runtime.Verify(r => r.WaitUntilIdleAsync(It.IsAny<CancellationToken>()), Times.Once);

        var cancelIndex = harness.Calls.IndexOf("cancel");
        var idleIndex = harness.Calls.IndexOf("wait-idle");
        var setRootIndex = harness.Calls.IndexOf("set-root");
        cancelIndex.Should().BeLessThan(idleIndex);
        idleIndex.Should().BeLessThan(setRootIndex, "改根必须等执行真的停下来");
    }

    [Fact]
    public async Task Apply_WhenReloadReportsFailure_ShouldRollBackAndReportError()
    {
        var harness = new Harness(inFlight: false);
        harness.Orchestrator.Results = [new ReloadResult { ComponentId = "session", Success = false, Error = "boom" }];

        var result = await harness.Switcher.ApplyAsync(
            new WorkspaceApplyRequest(harness.NewRoot),
            TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("session");
        harness.Workspace.Verify(w => w.SetWorkspaceRoot(harness.OldRoot), Times.Once, "失败要回滚旧根");
        harness.History.Verify(h => h.RecordOpened(It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
        harness.Participant.Applied.Should().Be(0);
    }

    private sealed class Harness
    {
        public Harness(bool inFlight)
        {
            OldRoot = Path.Combine(Path.GetTempPath(), "seeing-old-" + Guid.NewGuid().ToString("N"));
            NewRoot = Path.Combine(Path.GetTempPath(), "seeing-new-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(OldRoot);
            Directory.CreateDirectory(NewRoot);

            Workspace = new Mock<IWorkspaceProvider>();
            Workspace.Setup(w => w.IsResolved).Returns(true);
            Workspace.Setup(w => w.ResolvedRoot).Returns(OldRoot);

            Runtime = new Mock<IExecutionInFlightBoundary>();
            Runtime.Setup(r => r.HasInFlight()).Returns(inFlight);
            Runtime.Setup(r => r.WaitUntilIdleAsync(It.IsAny<CancellationToken>()))
                .Callback(() => Calls.Add("wait-idle"))
                .Returns(Task.CompletedTask);
            Runtime.Setup(r => r.CancelAllInFlightAsync(It.IsAny<CancellationToken>()))
                .Callback(() => Calls.Add("cancel"))
                .ReturnsAsync(1);

            Permissions = new Mock<IPermissionRequestManager>();
            Permissions.Setup(p => p.GetAllPending()).Returns(Array.Empty<PermissionRequest>());

            var config = new Mock<UnifiedConfigManager>(
                Mock.Of<IWorkspaceProvider>(), NullLogger<UnifiedConfigManager>.Instance,
                ConfigSectionRegistry.CreateWithSpine());
            History = new Mock<IWorkspaceHistoryStore>();

            Orchestrator = new Mock<ReloadSignalStub>();

            Participant = new RecordingParticipant(Calls, Events);

            Switcher = new WorkspaceSwitcher(
                Workspace.Object,
                config.Object,
                History.Object,
                new ReloadStub(() => { Calls.Add("reload"); Events.Add("reload"); }),
                Runtime.Object,
                Permissions.Object,
                new[] { Participant },
                NullLogger<WorkspaceSwitcher>.Instance);
        }

        public string OldRoot { get; }
        public string NewRoot { get; }
        public Mock<IWorkspaceProvider> Workspace { get; }
        public Mock<IExecutionInFlightBoundary> Runtime { get; }
        public Mock<IPermissionRequestManager> Permissions { get; }
        public Mock<IWorkspaceHistoryStore> History { get; }
        public Mock<ReloadSignalStub> Orchestrator { get; }
        public RecordingParticipant Participant { get; }
        public List<string> Calls { get; } = new();
        public List<string> Events { get; } = new();
        public WorkspaceSwitcher Switcher { get; }

        public void Dispose()
        {
            foreach (var dir in new[] { OldRoot, NewRoot })
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (IOException) { }
            }
        }
    }

    private sealed class RecordingParticipant : IWorkspaceApplyParticipant
    {
        private readonly List<string> _calls;
        private readonly List<string> _events;

        public RecordingParticipant(List<string> calls, List<string> events)
        {
            _calls = calls;
            _events = events;
        }

        public int Applying { get; private set; }
        public int Applied { get; private set; }
        public string ComponentId => "recording";

        public Task OnApplyingAsync(WorkspaceApplyContext context, CancellationToken ct)
        {
            Applying++;
            _events.Add("applying");
            return Task.CompletedTask;
        }

        public Task OnAppliedAsync(WorkspaceApplyContext context, CancellationToken ct)
        {
            Applied++;
            _events.Add("applied");
            return Task.CompletedTask;
        }
    }

    public abstract class ReloadSignalStub { }

    private sealed class ReloadStub : IWorkspaceReloadInvoker
    {
        private readonly Action _onReload;
        public ReloadStub(Action onReload) => _onReload = onReload;

        public Task<IReadOnlyList<ReloadResult>> ReloadAsync(WorkspaceApplyContext ctx, CancellationToken ct)
        {
            _onReload();
            return Task.FromResult<IReadOnlyList<ReloadResult>>(Array.Empty<ReloadResult>());
        }
    }
}
```

说明：`UnifiedConfigManager` 的 mock 依赖其构造函数签名；若构造函数参数不同，改为定义一个窄接口 `IWorkspaceConfigReloader { Task ReloadAsync(CancellationToken ct); }` 并在 DI 中把 `UnifiedConfigManager.LoadAsync` 包一层。**选择后者更稳**：Task 8 里新增该接口与其适配器，`WorkspaceSwitcher` 依赖窄接口，测试用 `Mock<IWorkspaceConfigReloader>`，去掉 `UnifiedConfigManager` 的 mock 与 `ReloadSignalStub` 占位类型。

对应实现签名：

```csharp
public interface IWorkspaceConfigReloader { Task ReloadAsync(CancellationToken ct = default); }
public interface IWorkspaceReloadInvoker { Task<IReadOnlyList<ReloadResult>> ReloadAsync(WorkspaceApplyContext ctx, CancellationToken ct); }
```

- [ ] **Step 2: 运行确认失败**

Expected: `WorkspaceSwitcher` 不存在。

- [ ] **Step 3: 实现**

```csharp
// src/hosting/Seeing.Agent.Hosting/Execution/WorkspaceSwitcher.cs
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Configuration;

namespace Seeing.Agent.Hosting.Execution;

/// <summary>一次工作区切换请求。</summary>
public sealed record WorkspaceApplyRequest(string NewRoot);

/// <summary>切换结果。失败时调用方不得导航到新工作区。</summary>
public sealed record WorkspaceApplyResult(
    bool Success,
    string? Error,
    IReadOnlyList<ReloadResult> ReloadResults)
{
    public static WorkspaceApplyResult Failed(string error) =>
        new(false, error, Array.Empty<ReloadResult>());
}

public interface IWorkspaceSwitcher
{
    Task<WorkspaceApplyResult> ApplyAsync(WorkspaceApplyRequest request, CancellationToken ct = default);
}

public interface IWorkspaceConfigReloader
{
    Task ReloadAsync(CancellationToken ct = default);
}

public interface IWorkspaceReloadInvoker
{
    Task<IReadOnlyList<ReloadResult>> ReloadAsync(WorkspaceApplyContext ctx, CancellationToken ct);
}

/// <summary>
/// 唯一的工作区切换实现：首次选定与再次切换共用。
/// <para>
/// 顺序不可调换：先拒绝未决审批，再取消并**等到空闲**，然后才改根。
/// 取消信号发出时执行循环还没收尾，此时改根会让收尾写进新工作区的存储。
/// </para>
/// </summary>
public sealed class WorkspaceSwitcher : IWorkspaceSwitcher
{
    private readonly IWorkspaceProvider _workspace;
    private readonly IWorkspaceConfigReloader _configReloader;
    private readonly IWorkspaceHistoryStore _history;
    private readonly IWorkspaceReloadInvoker _reloader;
    private readonly IExecutionInFlightBoundary? _inFlight;
    private readonly IPermissionRequestManager? _permissions;
    private readonly IReadOnlyList<IWorkspaceApplyParticipant> _participants;
    private readonly ILogger<WorkspaceSwitcher> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WorkspaceSwitcher(
        IWorkspaceProvider workspace,
        IWorkspaceConfigReloader configReloader,
        IWorkspaceHistoryStore history,
        IWorkspaceReloadInvoker reloader,
        IExecutionInFlightBoundary? inFlight,
        IPermissionRequestManager? permissions,
        IEnumerable<IWorkspaceApplyParticipant> participants,
        ILogger<WorkspaceSwitcher> logger)
    {
        _workspace = workspace;
        _configReloader = configReloader;
        _history = history;
        _reloader = reloader;
        _inFlight = inFlight;
        _permissions = permissions;
        _participants = participants.ToArray();
        _logger = logger;
    }

    public async Task<WorkspaceApplyResult> ApplyAsync(
        WorkspaceApplyRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.NewRoot) || !Directory.Exists(request.NewRoot))
            return WorkspaceApplyResult.Failed($"目录不存在: {request.NewRoot}");

        if (!await _gate.WaitAsync(TimeSpan.Zero, ct).ConfigureAwait(false))
            return WorkspaceApplyResult.Failed("上一次工作区切换尚未完成");

        try
        {
            var newRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.NewRoot));
            var oldRoot = _workspace.ResolvedRoot;

            if (oldRoot is not null && IsSame(oldRoot, newRoot))
                return new WorkspaceApplyResult(true, null, Array.Empty<ReloadResult>());

            var inFlight = _inFlight?.HasInFlight() == true;

            // 1. 先拒绝未决审批：取消执行后，卡在审批上的工具会靠这些拒绝解开。
            RejectPendingPermissions();

            // 2. 取消在途，并等到它真的收尾。
            if (inFlight && _inFlight is not null)
            {
                var cancelled = await _inFlight.CancelAllInFlightAsync(ct).ConfigureAwait(false);
                _logger.LogInformation("工作区切换：已取消 {Count} 个在途执行", cancelled);
                await _inFlight.WaitUntilIdleAsync(ct).ConfigureAwait(false);
            }

            var context = new WorkspaceApplyContext(oldRoot, newRoot);

            // 3. 停掉跑在旧根上的运行时（Gateway / 渠道子进程 / 调度器）。
            foreach (var participant in _participants)
            {
                try
                {
                    await participant.OnApplyingAsync(context, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "参与者 {Component} 在切换前停止失败", participant.ComponentId);
                    return WorkspaceApplyResult.Failed($"{participant.ComponentId} 停止失败: {ex.Message}");
                }
            }

            try
            {
                // 4. 改根 → 重读项目配置 → 一轮显式重载。
                _workspace.SetWorkspaceRoot(newRoot);
                await _configReloader.ReloadAsync(ct).ConfigureAwait(false);

                var results = await _reloader.ReloadAsync(context, ct).ConfigureAwait(false);
                var failed = results.Where(r => !r.Success).ToArray();
                if (failed.Length > 0)
                {
                    var detail = string.Join("; ", failed.Select(f => $"{f.ComponentId}: {f.Error}"));
                    return await RollbackAsync(oldRoot, $"重载失败: {detail}", ct).ConfigureAwait(false);
                }

                // 5. 记录历史（切换是用户显式选择，改 lastInteractive）。
                _history.RecordOpened(newRoot, asDefault: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "工作区切换失败: {NewRoot}", newRoot);
                return await RollbackAsync(oldRoot, ex.Message, ct).ConfigureAwait(false);
            }

            // 6. 按新根重新启动。
            foreach (var participant in _participants)
            {
                try
                {
                    await participant.OnAppliedAsync(context, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "参与者 {Component} 在新根启动失败", participant.ComponentId);
                }
            }

            return new WorkspaceApplyResult(true, null, Array.Empty<ReloadResult>());
        }
        finally
        {
            _gate.Release();
        }
    }

    private void RejectPendingPermissions()
    {
        if (_permissions is null)
            return;

        foreach (var pending in _permissions.GetAllPending())
        {
            if (string.IsNullOrEmpty(pending.RequestId))
                continue;

            _permissions.TryResolve(
                pending.RequestId, PermissionEffect.Deny, PermissionGrantScope.Once,
                PermissionResolvedBy.Cancellation, "工作区切换", pending.SessionId);
        }
    }

    private async Task<WorkspaceApplyResult> RollbackAsync(string? oldRoot, string error, CancellationToken ct)
    {
        if (oldRoot is null)
            return WorkspaceApplyResult.Failed(error);

        try
        {
            _workspace.SetWorkspaceRoot(oldRoot);
            await _configReloader.ReloadAsync(ct).ConfigureAwait(false);
            await _reloader.ReloadAsync(
                new WorkspaceApplyContext(null, oldRoot), ct).ConfigureAwait(false);
            _logger.LogWarning("工作区切换已回滚到 {OldRoot}: {Error}", oldRoot, error);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "工作区回滚失败，停在错误状态: {OldRoot}", oldRoot);
            return WorkspaceApplyResult.Failed($"{error}；回滚也失败: {ex.Message}");
        }

        return WorkspaceApplyResult.Failed(error);
    }

    private static bool IsSame(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            PathBoundary.PathComparison);
}
```

`ReloadResult` 的命名空间是 `Seeing.Agent.Abstractions.Configuration`（见现有 `ReloadSignalBus.cs`），确认 `using` 到位。

- [ ] **Step 4: 适配器与注册**

```csharp
// src/hosting/Seeing.Agent.Hosting/Execution/WorkspaceReloadAdapters.cs
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Configuration;

namespace Seeing.Agent.Hosting.Execution;

/// <summary>把 <see cref="UnifiedConfigManager.LoadAsync"/> 收窄成切换流程需要的一步。</summary>
internal sealed class WorkspaceConfigReloader : IWorkspaceConfigReloader
{
    private readonly UnifiedConfigManager _config;
    public WorkspaceConfigReloader(UnifiedConfigManager config) => _config = config;

    public Task ReloadAsync(CancellationToken ct = default) => _config.LoadAsync(ct);
}

/// <summary>
/// 一轮显式全量重载：抑制编排器的自动触发，自己跑 WorkspaceChange + ConfigChange，
/// 并把每个处理器的成败返回给调用方（编排器不会把处理器失败抛出来）。
/// </summary>
internal sealed class WorkspaceReloadInvoker : IWorkspaceReloadInvoker
{
    private readonly ReloadOrchestrator _orchestrator;
    private readonly ILogger<WorkspaceReloadInvoker> _logger;

    public WorkspaceReloadInvoker(ReloadOrchestrator orchestrator, ILogger<WorkspaceReloadInvoker> logger)
    {
        _orchestrator = orchestrator;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ReloadResult>> ReloadAsync(
        WorkspaceApplyContext ctx, CancellationToken ct)
    {
        using var _ = _orchestrator.SuppressAutoReload();

        var workspaceResults = await _orchestrator.ReloadAsync(
            new WorkspaceChange
            {
                OldWorkspace = ctx.OldRoot ?? string.Empty,
                NewWorkspace = ctx.NewRoot,
            }, ct).ConfigureAwait(false);

        var configResults = await _orchestrator.ReloadAsync(
            new ConfigChange { ChangedSections = Array.Empty<string>() }, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "工作区重载完成: {WorkspaceCount} 个组件（工作区）+ {ConfigCount} 个组件（配置）",
            workspaceResults.Count, configResults.Count);

        return workspaceResults.Concat(configResults).ToArray();
    }
}
```

注册（`AddHostShapeBaseline` 或 Web/Gateway/Headless 基线扩展里）：

```csharp
        services.TryAddSingleton<IWorkspaceConfigReloader, WorkspaceConfigReloader>();
        services.TryAddSingleton<IWorkspaceReloadInvoker, WorkspaceReloadInvoker>();
        services.TryAddSingleton<IWorkspaceSwitcher, WorkspaceSwitcher>();
```

- [ ] **Step 5: 运行测试确认通过**

```bash
dotnet build tests/hosting/Seeing.Agent.Hosting.Tests
dotnet vstest tests/hosting/Seeing.Agent.Hosting.Tests/bin/Debug/net10.0/Seeing.Agent.Hosting.Tests.dll --TestCaseFilter:"FullyQualifiedName~WorkspaceSwitcherTests"
```
Expected: 3 passed。

- [ ] **Step 6: 提交**

```bash
git add src/hosting/Seeing.Agent.Hosting/Execution/WorkspaceSwitcher.cs src/hosting/Seeing.Agent.Hosting/Execution/WorkspaceReloadAdapters.cs src/hosting/Seeing.Agent.Hosting/ tests/hosting/Seeing.Agent.Hosting.Tests/Execution/WorkspaceSwitcherTests.cs
git commit -m "feat(hosting): 唯一的工作区切换实现"
```

---

### Task 9: 能力包参与者 —— Gateway 与调度器

**Files:**
- Create: `src/gateway/Seeing.Agent.Gateway/Hosting/GatewayWorkspaceParticipant.cs`
- Create: `src/capabilities/Seeing.Agent.Scheduler/Configuration/SchedulerWorkspaceParticipant.cs`
- Modify: 两个模块的 DI 注册
- Test: `tests/gateway/Seeing.Agent.Gateway.Tests/Hosting/GatewayWorkspaceParticipantTests.cs`、`tests/capabilities/Seeing.Agent.Scheduler.Tests/SchedulerWorkspaceParticipantTests.cs`

**Interfaces:**
- Consumes: `IWorkspaceApplyParticipant`（Task 7）
- Produces: 两个 `IWorkspaceApplyParticipant` 实现，`ComponentId` 分别为 `"gateway"` 与 `"scheduler"`

- [ ] **Step 1: 写 Gateway 参与者测试**

```csharp
// tests/gateway/Seeing.Agent.Gateway.Tests/Hosting/GatewayWorkspaceParticipantTests.cs
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Configuration;
using Seeing.Agent.Gateway.Channels;
using Seeing.Agent.Gateway.Hosting;
using Xunit;

namespace Seeing.Agent.Gateway.Tests.Hosting;

public class GatewayWorkspaceParticipantTests
{
    [Fact]
    public async Task OnApplying_WhenGatewayRunning_ShouldStopGatewayAndChannelHosts()
    {
        var server = new Mock<IGatewayServer>();
        var hosts = new Mock<IChannelHostController>();
        var participant = new GatewayWorkspaceParticipant(
            server.Object, hosts.Object, NullLogger<GatewayWorkspaceParticipant>.Instance);

        await participant.OnApplyingAsync(
            new WorkspaceApplyContext(@"C:\a", @"C:\b"),
            TestContext.Current.CancellationToken);

        hosts.Verify(h => h.StopAllAsync(It.IsAny<CancellationToken>()), Times.Once);
        server.Verify(s => s.StopAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OnApplied_WhenGatewayWasEnabled_ShouldStartOnNewRoot()
    {
        var server = new Mock<IGatewayServer>();
        var hosts = new Mock<IChannelHostController>();
        var participant = new GatewayWorkspaceParticipant(
            server.Object, hosts.Object, NullLogger<GatewayWorkspaceParticipant>.Instance)
        {
            WasEnabled = true,
        };

        await participant.OnAppliedAsync(
            new WorkspaceApplyContext(@"C:\a", @"C:\b"),
            TestContext.Current.CancellationToken);

        server.Verify(s => s.StartAsync(It.IsAny<CancellationToken>()), Times.Once);
        hosts.Verify(h => h.StartAllAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OnApplied_WhenGatewayWasDisabled_ShouldNotStart()
    {
        var server = new Mock<IGatewayServer>();
        var hosts = new Mock<IChannelHostController>();
        var participant = new GatewayWorkspaceParticipant(
            server.Object, hosts.Object, NullLogger<GatewayWorkspaceParticipant>.Instance)
        {
            WasEnabled = false,
        };

        await participant.OnAppliedAsync(
            new WorkspaceApplyContext(@"C:\a", @"C:\b"),
            TestContext.Current.CancellationToken);

        server.Verify(s => s.StartAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
```

- [ ] **Step 2: 实现 Gateway 参与者**

```csharp
// src/gateway/Seeing.Agent.Gateway/Hosting/GatewayWorkspaceParticipant.cs
using Microsoft.Extensions.Logging;
using Seeing.Agent.Configuration;
using Seeing.Agent.Gateway.Channels;

namespace Seeing.Agent.Gateway.Hosting;

/// <summary>
/// 工作区切换时停掉 Gateway 与渠道子进程，重载完成后再按新根拉起。
/// <para>
/// 渠道子进程有自己的 <c>WorkspaceProvider</c>（构造时按配置路径推断），
/// 不会跟着 WebUI 的 <c>SetWorkspaceRoot</c> 走，必须真的重启。
/// </para>
/// </summary>
public sealed class GatewayWorkspaceParticipant : IWorkspaceApplyParticipant
{
    private readonly IGatewayServer _server;
    private readonly IChannelHostController _channelHosts;
    private readonly ILogger<GatewayWorkspaceParticipant> _logger;

    public GatewayWorkspaceParticipant(
        IGatewayServer server,
        IChannelHostController channelHosts,
        ILogger<GatewayWorkspaceParticipant> logger)
    {
        _server = server;
        _channelHosts = channelHosts;
        _logger = logger;
    }

    /// <summary>切换前 Gateway 是否在运行（由 <see cref="OnApplyingAsync"/> 记录）。</summary>
    public bool WasEnabled { get; set; }

    public string ComponentId => "gateway";

    public async Task OnApplyingAsync(WorkspaceApplyContext context, CancellationToken ct)
    {
        WasEnabled = _server.IsRunning;

        await _channelHosts.StopAllAsync(ct).ConfigureAwait(false);

        if (WasEnabled)
            await _server.StopAsync(ct).ConfigureAwait(false);

        _logger.LogInformation("工作区切换：Gateway 已停止（切换前运行={WasEnabled}）", WasEnabled);
    }

    public async Task OnAppliedAsync(WorkspaceApplyContext context, CancellationToken ct)
    {
        if (!WasEnabled)
            return;

        await _server.StartAsync(ct).ConfigureAwait(false);
        await _channelHosts.StartAllAsync(ct).ConfigureAwait(false);

        _logger.LogInformation("工作区切换：Gateway 已按新工作区启动: {Root}", context.NewRoot);
    }
}
```

新增 `IChannelHostController` 契约（放在 `src/gateway/Seeing.Agent.Gateway/Channels/`）：

```csharp
namespace Seeing.Agent.Gateway.Channels;

/// <summary>批量启停渠道子进程（工作区切换与宿主开关共用）。</summary>
public interface IChannelHostController
{
    Task StopAllAsync(CancellationToken ct = default);
    Task StartAllAsync(CancellationToken ct = default);
}
```

实现 `ChannelHostController`：`StopAllAsync` 遍历 `ChannelHostConfigStore.GetChannelHosts()` 里 `ProcessId` 存活的条目调 `ChannelHostManager.StopAsync`；`StartAllAsync` 对 `Enabled` 的条目调 `StartAsync`。注册 `services.TryAddSingleton<IChannelHostController, ChannelHostController>()`。

- [ ] **Step 3: 写 Scheduler 参与者测试**

```csharp
// tests/capabilities/Seeing.Agent.Scheduler.Tests/SchedulerWorkspaceParticipantTests.cs
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Configuration;
using Seeing.Agent.Scheduler.Configuration;
using Seeing.Agent.Scheduler.Engine;
using Xunit;

namespace Seeing.Agent.Scheduler.Tests;

public class SchedulerWorkspaceParticipantTests
{
    [Fact]
    public async Task OnApplying_ShouldStopEngine_AndRememberState()
    {
        var engine = new Mock<IQuartzSchedulerLifecycle>();
        engine.SetupGet(e => e.IsStarted).Returns(true);

        var participant = new SchedulerWorkspaceParticipant(
            engine.Object, NullLogger<SchedulerWorkspaceParticipant>.Instance);

        await participant.OnApplyingAsync(
            new WorkspaceApplyContext(@"C:\a", @"C:\b"), TestContext.Current.CancellationToken);

        engine.Verify(e => e.StopAsync(It.IsAny<CancellationToken>()), Times.Once);
        participant.WasStarted.Should().BeTrue();
    }

    [Fact]
    public async Task OnApplied_WhenWasStarted_ShouldStartNewEngine()
    {
        var engine = new Mock<IQuartzSchedulerLifecycle>();
        var participant = new SchedulerWorkspaceParticipant(
            engine.Object, NullLogger<SchedulerWorkspaceParticipant>.Instance);

        await participant.OnApplyingAsync(
            new WorkspaceApplyContext(@"C:\a", @"C:\b"), TestContext.Current.CancellationToken);
        await participant.OnAppliedAsync(
            new WorkspaceApplyContext(@"C:\a", @"C:\b"), TestContext.Current.CancellationToken);

        engine.Verify(e => e.RebuildAsync(It.IsAny<CancellationToken>()), Times.Once,
            "数据库连接串在注册时写死，切换后必须按新根重建");
    }
}
```

- [ ] **Step 4: 实现 Scheduler 参与者**

```csharp
// src/capabilities/Seeing.Agent.Scheduler/Engine/IQuartzSchedulerLifecycle.cs
namespace Seeing.Agent.Scheduler.Engine;

/// <summary>调度器生命周期（供工作区切换停/重建）。</summary>
public interface IQuartzSchedulerLifecycle
{
    bool IsStarted { get; }
    Task StopAsync(CancellationToken ct = default);
    Task StartAsync(CancellationToken ct = default);

    /// <summary>丢弃已构造的 scheduler 实例，下次 <see cref="StartAsync"/> 按当前工作区重建。</summary>
    Task RebuildAsync(CancellationToken ct = default);
}
```

`QuartzSchedulerEngine` 实现该接口：`IsStarted => _scheduler?.IsStarted == true`；`RebuildAsync` 在 `_lock` 内 `Shutdown` 并把 `_scheduler = null`。

```csharp
// src/capabilities/Seeing.Agent.Scheduler/Configuration/SchedulerWorkspaceParticipant.cs
using Microsoft.Extensions.Logging;
using Seeing.Agent.Configuration;
using Seeing.Agent.Scheduler.Engine;

namespace Seeing.Agent.Scheduler.Configuration;

/// <summary>工作区切换时停掉调度器；重载完成后按新根重建（SQLite 连接串在注册时写死）。</summary>
public sealed class SchedulerWorkspaceParticipant : IWorkspaceApplyParticipant
{
    private readonly IQuartzSchedulerLifecycle _engine;
    private readonly ILogger<SchedulerWorkspaceParticipant> _logger;

    public SchedulerWorkspaceParticipant(
        IQuartzSchedulerLifecycle engine,
        ILogger<SchedulerWorkspaceParticipant> logger)
    {
        _engine = engine;
        _logger = logger;
    }

    public bool WasStarted { get; set; }

    public string ComponentId => "scheduler";

    public async Task OnApplyingAsync(WorkspaceApplyContext context, CancellationToken ct)
    {
        WasStarted = _engine.IsStarted;
        await _engine.StopAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("工作区切换：调度器已停止（切换前运行={WasStarted}）", WasStarted);
    }

    public async Task OnAppliedAsync(WorkspaceApplyContext context, CancellationToken ct)
    {
        await _engine.RebuildAsync(ct).ConfigureAwait(false);

        if (WasStarted)
            await _engine.StartAsync(ct).ConfigureAwait(false);

        _logger.LogInformation("工作区切换：调度器已按新工作区重建: {Root}", context.NewRoot);
    }
}
```

注册进 `AddSeeingScheduler`：`services.TryAddEnumerable(ServiceDescriptor.Singleton<IWorkspaceApplyParticipant, SchedulerWorkspaceParticipant>());`

- [ ] **Step 5: 运行测试**

```bash
dotnet build tests/gateway/Seeing.Agent.Gateway.Tests
dotnet vstest tests/gateway/Seeing.Agent.Gateway.Tests/bin/Debug/net10.0/Seeing.Agent.Gateway.Tests.dll --TestCaseFilter:"FullyQualifiedName~GatewayWorkspaceParticipantTests"
dotnet build tests/capabilities/Seeing.Agent.Scheduler.Tests
dotnet vstest tests/capabilities/Seeing.Agent.Scheduler.Tests/bin/Debug/net10.0/Seeing.Agent.Scheduler.Tests.dll --TestCaseFilter:"FullyQualifiedName~SchedulerWorkspaceParticipantTests"
```
Expected: 3 + 2 passed。

- [ ] **Step 6: 提交**

```bash
git add src/gateway/Seeing.Agent.Gateway/Hosting/GatewayWorkspaceParticipant.cs src/gateway/Seeing.Agent.Gateway/Channels/IChannelHostController.cs src/gateway/Seeing.Agent.Gateway/Channels/ChannelHostController.cs src/capabilities/Seeing.Agent.Scheduler/Configuration/SchedulerWorkspaceParticipant.cs src/capabilities/Seeing.Agent.Scheduler/Engine/IQuartzSchedulerLifecycle.cs tests/gateway/Seeing.Agent.Gateway.Tests/Hosting/GatewayWorkspaceParticipantTests.cs tests/capabilities/Seeing.Agent.Scheduler.Tests/SchedulerWorkspaceParticipantTests.cs
git commit -m "feat(gateway,scheduler): 工作区切换参与者"
```

---

### Task 10: 目录浏览服务

**Files:**
- Create: `samples/Seeing.Agent.WebUI/Services/DirectoryBrowser.cs`
- Modify: `samples/Seeing.Agent.WebUI/Hosting/WebUiHost.cs`（注册）
- Test: `tests/apps/Seeing.Agent.WebUI.Tests/Services/DirectoryBrowserTests.cs`

**Interfaces:**
- Produces:
  - `record DirectoryEntryView(string Name, string FullPath)`
  - `record DirectoryListing(string Path, string? ParentPath, IReadOnlyList<DirectoryEntryView> Directories, string? Error)`
  - `interface IDirectoryBrowser { IReadOnlyList<DirectoryEntryView> GetRoots(); DirectoryListing List(string path); DirectoryListing CreateSubdirectory(string parentPath, string name); Task<DirectoryListing?> ConfirmAsync(string path); }`
  - `sealed class DirectoryBrowser : IDirectoryBrowser`（Singleton，无状态）

- [ ] **Step 1: 写失败测试**

```csharp
// tests/apps/Seeing.Agent.WebUI.Tests/Services/DirectoryBrowserTests.cs
using FluentAssertions;
using Seeing.Agent.WebUI.Services;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Services;

public class DirectoryBrowserTests
{
    [Fact]
    public void List_ShouldReturnOnlyDirectories_SortedByName()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(Path.Combine(dir.Root, "b"));
        Directory.CreateDirectory(Path.Combine(dir.Root, "A"));
        File.WriteAllText(Path.Combine(dir.Root, "file.txt"), "x");

        var listing = new DirectoryBrowser().List(dir.Root);

        listing.Error.Should().BeNull();
        listing.Directories.Select(d => d.Name).Should().Equal("A", "b");
    }

    [Fact]
    public void List_MissingPath_ShouldReturnError_WithoutThrowing()
    {
        var listing = new DirectoryBrowser().List(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid()));

        listing.Error.Should().NotBeNull();
        listing.Directories.Should().BeEmpty();
    }

    [Fact]
    public void CreateSubdirectory_ShouldCreateAndReturnListing()
    {
        using var dir = new TempDir();

        var listing = new DirectoryBrowser().CreateSubdirectory(dir.Root, "new-folder");

        listing.Error.Should().BeNull();
        Directory.Exists(Path.Combine(dir.Root, "new-folder")).Should().BeTrue();
        listing.Directories.Select(d => d.Name).Should().Contain("new-folder");
    }

    [Theory]
    [InlineData(@"..\escape")]
    [InlineData("../escape")]
    [InlineData(@"a\b")]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateSubdirectory_InvalidName_ShouldReturnError(string name)
    {
        using var dir = new TempDir();

        var listing = new DirectoryBrowser().CreateSubdirectory(dir.Root, name);

        listing.Error.Should().NotBeNull();
        Directory.GetDirectories(dir.Root).Should().BeEmpty();
    }

    [Fact]
    public void CreateSubdirectory_OutsideAllowedParent_ShouldReturnError()
    {
        using var allowed = new TempDir();
        using var outside = new TempDir();
        var browser = new DirectoryBrowser();

        var listing = browser.CreateSubdirectory(outside.Root, "x");

        listing.Error.Should().NotBeNull("父目录必须是本次会话已经列出的目录");
    }

    [Fact]
    public void GetRoots_ShouldNotBeEmpty() =>
        new DirectoryBrowser().GetRoots().Should().NotBeEmpty();

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Root = Path.Combine(Path.GetTempPath(), "seeing-dir-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, true);
            }
            catch (IOException)
            {
            }
        }
    }
}
```

- [ ] **Step 2: 运行确认失败**

Expected: `DirectoryBrowser` 不存在。

- [ ] **Step 3: 实现**

```csharp
// samples/Seeing.Agent.WebUI/Services/DirectoryBrowser.cs
namespace Seeing.Agent.WebUI.Services;

public sealed record DirectoryEntryView(string Name, string FullPath);

public sealed record DirectoryListing(
    string Path,
    string? ParentPath,
    IReadOnlyList<DirectoryEntryView> Directories,
    string? Error);

public interface IDirectoryBrowser
{
    IReadOnlyList<DirectoryEntryView> GetRoots();
    DirectoryListing List(string path);
    DirectoryListing CreateSubdirectory(string parentPath, string name);
}

/// <summary>
/// 进程内的目录浏览。仅供 <c>DirectoryPicker</c> 使用。
/// <para>
/// 为什么不复用 <c>Seeing.Agent.Files</c> / <c>WebUI.Files</c>：那些能力只描述当前工作区，
/// 而选新工作区时必须能看到工作区之外。这里不调用 <c>IWorkspacePathGate</c> 或 <c>PathBoundary</c>：
/// 它不判断「是否在根内」，所以不该走那道门；越界保护靠下面「父目录必须是本次列出的目录」。
/// </para>
/// <para>
/// 信任模型与页面一致：能打开 WebUI 就能调用。WebUI 没有独立登录，
/// 部署到非回环地址时本服务与页面同面暴露；不单独加认证。
/// </para>
/// </summary>
public sealed class DirectoryBrowser : IDirectoryBrowser
{
    // 本次进程里已经列给用户的目录。创建只允许发生在这些目录之下。
    private readonly HashSet<string> _listed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    public IReadOnlyList<DirectoryEntryView> GetRoots()
    {
        var roots = new List<DirectoryEntryView>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            bool ready;
            try { ready = drive.IsReady; } catch (IOException) { ready = false; }
            if (!ready) continue;

            roots.Add(new DirectoryEntryView(drive.Name.TrimEnd('\\', '/'), drive.RootDirectory.FullName));
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home) && Directory.Exists(home))
        {
            var name = new DirectoryInfo(home).Name;
            if (roots.All(r => !IsSame(r.FullPath, home)))
                roots.Insert(0, new DirectoryEntryView($"主目录（{name}）", home));
        }

        return roots;
    }

    public DirectoryListing List(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            path = Directory.Exists(home) ? home : GetRoots().FirstOrDefault()?.FullPath ?? "/";
        }

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex)
        {
            return Failed(path, $"路径无法识别: {ex.Message}");
        }

        if (!Directory.Exists(full))
            return Failed(full, "目录不存在");

        try
        {
            var entries = new DirectoryInfo(full)
                .EnumerateDirectories()
                .Where(d => (d.Attributes & FileAttributes.System) == 0)
                .Select(d => new DirectoryEntryView(d.Name, d.FullName))
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            MarkListed(full);

            return new DirectoryListing(full, ParentOf(full), entries, null);
        }
        catch (UnauthorizedAccessException)
        {
            return Failed(full, "没有访问权限");
        }
        catch (IOException ex)
        {
            return Failed(full, ex.Message);
        }
    }

    public DirectoryListing CreateSubdirectory(string parentPath, string name)
    {
        if (!IsListed(parentPath))
            return Failed(parentPath, "只能在本对话框已列出的目录下新建文件夹");

        if (string.IsNullOrWhiteSpace(name)
            || name.Contains(Path.DirectorySeparatorChar)
            || name.Contains(Path.AltDirectorySeparatorChar)
            || name.Contains("..", StringComparison.Ordinal)
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return Failed(parentPath, "文件夹名称不合法");
        }

        var target = Path.Combine(parentPath, name.Trim());
        try
        {
            Directory.CreateDirectory(target);
            return List(parentPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Failed(parentPath, $"新建失败: {ex.Message}");
        }
    }

    private void MarkListed(string path)
    {
        lock (_gate)
            _listed.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
    }

    private bool IsListed(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception)
        {
            return false;
        }

        lock (_gate)
            return _listed.Contains(full);
    }

    private static DirectoryListing Failed(string path, string error) =>
        new(path, null, Array.Empty<DirectoryEntryView>(), error);

    private static string? ParentOf(string path)
    {
        var parent = Path.GetDirectoryName(path);
        return string.IsNullOrEmpty(parent) ? null : parent;
    }

    private static bool IsSame(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);
}
```

注册：`builder.Services.AddSingleton<IDirectoryBrowser, DirectoryBrowser>();`

- [ ] **Step 4: 运行测试确认通过**

```bash
dotnet build tests/apps/Seeing.Agent.WebUI.Tests
dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll --TestCaseFilter:"FullyQualifiedName~DirectoryBrowserTests"
```
Expected: 8 passed（含 Theory 5 条）。

- [ ] **Step 5: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Services/DirectoryBrowser.cs samples/Seeing.Agent.WebUI/Hosting/WebUiHost.cs tests/apps/Seeing.Agent.WebUI.Tests/Services/DirectoryBrowserTests.cs
git commit -m "feat(webui): 进程内目录浏览服务"
```

---

### Task 11: 目录选择对话框

**Files:**
- Create: `samples/Seeing.Agent.WebUI/Components/DirectoryPicker.razor`

**Interfaces:**
- Consumes: `IDirectoryBrowser`（Task 10）
- Produces: `<DirectoryPicker @ref="_picker" Title="选择工作区" InitialPath="..." OnSelected="EventCallback&lt;string&gt;" />`，公开 `void Show()` / `void Hide()`

- [ ] **Step 1: 写组件**

```razor
@using AntDesign
@using Seeing.Agent.WebUI.Services
@inject IDirectoryBrowser Browser

<Modal @bind-Visible="_visible"
       Title="@Title"
       Centered="true"
       Width="720"
       OnOk="Confirm"
       OkText="使用此目录"
       OnCancel="Hide">
    @if (_listing is null)
    {
        <Spin />
    }
    else
    {
        <div class="dir-picker-path">
            <Button Size="@ButtonSize.Small"
                    Icon="@IconType.Outline.Home"
                    OnClick="GoRoots"
                    Title="磁盘根" />
            @if (_listing.ParentPath is not null)
            {
                <Button Size="@ButtonSize.Small"
                        Icon="@IconType.Outline.ArrowUp"
                        OnClick="@(() => Navigate(_listing.ParentPath))" />
            }
            <Text Code>@_listing.Path</Text>
        </div>

        @if (_showingRoots)
        {
            <div class="dir-picker-list">
                @foreach (var root in _roots)
                {
                    <div class="dir-picker-item" @onclick="@(() => Navigate(root.FullPath))">
                        <Icon Type="@IconType.Outline.Hdd" /> @root.Name
                    </div>
                }
            </div>
        }
        else
        {
            <div class="dir-picker-list">
                @foreach (var entry in _listing.Directories)
                {
                    <div class="dir-picker-item" @onclick="@(() => Navigate(entry.FullPath))">
                        <Icon Type="@IconType.Outline.Folder" /> @entry.Name
                    </div>
                }
                @if (_listing.Directories.Count == 0)
                {
                    <Empty Description="没有子目录" ImageSize="64" />
                }
            </div>
        }

        @if (_listing.Error is not null)
        {
            <Alert Type="@AlertType.Error" Message="@_listing.Error" ShowIcon="true" />
        }

        <Divider />
        <InputGroup Compact>
            <Input @bind-Value="_newFolderName" Placeholder="新建文件夹名称" />
            <Button OnClick="CreateFolder">新建文件夹</Button>
        </InputGroup>
    }
</Modal>

@code {
    private bool _visible;
    private bool _showingRoots;
    private string _newFolderName = "";
    private DirectoryListing? _listing;
    private IReadOnlyList<DirectoryEntryView> _roots = Array.Empty<DirectoryEntryView>();

    [Parameter] public string Title { get; set; } = "选择目录";

    /// <summary>打开时的起始目录；为空则从主目录开始。</summary>
    [Parameter] public string? InitialPath { get; set; }

    [Parameter] public EventCallback<string> OnSelected { get; set; }

    public void Show()
    {
        _visible = true;
        _newFolderName = "";
        _roots = Browser.GetRoots();
        _showingRoots = false;
        _listing = Browser.List(InitialPath ?? "");
        StateHasChanged();
    }

    public void Hide()
    {
        _visible = false;
        StateHasChanged();
    }

    private void Navigate(string path)
    {
        _showingRoots = false;
        _listing = Browser.List(path);
        _newFolderName = "";
    }

    private void GoRoots()
    {
        _roots = Browser.GetRoots();
        _showingRoots = true;
    }

    private void CreateFolder()
    {
        if (_listing is null)
            return;

        var result = Browser.CreateSubdirectory(_listing.Path, _newFolderName);
        _listing = result;
        if (result.Error is null)
            _newFolderName = "";
    }

    private async Task Confirm()
    {
        if (_showingRoots || _listing is null || _listing.Error is not null)
            return;

        _visible = false;
        await OnSelected.InvokeAsync(_listing.Path);
    }
}
```

样式追加到 `wwwroot/css/workspace-page.css`：

```css
.dir-picker-path { display: flex; align-items: center; gap: 8px; margin-bottom: 8px; }
.dir-picker-list { max-height: 320px; overflow-y: auto; border: 1px solid #f0f0f0; border-radius: 6px; padding: 4px; }
.dir-picker-item { padding: 6px 10px; border-radius: 4px; cursor: pointer; display: flex; gap: 8px; align-items: center; }
.dir-picker-item:hover { background: #f5f5f5; }
```

- [ ] **Step 2: 构建**

```bash
dotnet build samples/Seeing.Agent.WebUI
```
Expected: 0 error。

- [ ] **Step 3: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Components/DirectoryPicker.razor samples/Seeing.Agent.WebUI/wwwroot/css/workspace-page.css
git commit -m "feat(webui): 共用的目录选择对话框"
```

---

### Task 12: 工作区首页与路由守卫

**Files:**
- Create: `samples/Seeing.Agent.WebUI/Pages/WorkspacesPage.razor`
- Modify: `samples/Seeing.Agent.WebUI/Components/ModuleRouter.razor`
- Modify: `samples/Seeing.Agent.WebUI/Services/CoreShellUiContribution.cs`
- Test: `tests/apps/Seeing.Agent.WebUI.Tests/Services/WorkspaceGateTests.cs`

**Interfaces:**
- Consumes: `IWorkspaceSwitcher`（Task 8）、`IDirectoryPicker`（Task 11）、`IWorkspaceHistoryStore`
- Produces: `static bool WorkspaceGate.ShouldForceWorkspaceHome(bool isResolved, string currentPath)`

- [ ] **Step 1: 写路由守卫测试**

```csharp
// tests/apps/Seeing.Agent.WebUI.Tests/Services/WorkspaceGateTests.cs
using FluentAssertions;
using Seeing.Agent.WebUI.Services;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Services;

public class WorkspaceGateTests
{
    [Theory]
    [InlineData("")]
    [InlineData("sessions")]
    [InlineData("settings")]
    [InlineData("session/abc")]
    public void Unresolved_ShouldForceHome_ForEveryPathButHome(string path)
    {
        WorkspaceGate.ShouldForceWorkspaceHome(isResolved: false, relativePath: path)
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("workspaces")]
    [InlineData("workspaces/")]
    [InlineData("workspaces?x=1")]
    public void Unresolved_ShouldNotRedirectFromHome(string path) =>
        WorkspaceGate.ShouldForceWorkspaceHome(false, path).Should().BeFalse();

    [Fact]
    public void Resolved_ShouldNeverForceHome() =>
        WorkspaceGate.ShouldForceWorkspaceHome(true, "").Should().BeFalse();
}
```

- [ ] **Step 2: 实现守卫与首页**

```csharp
// samples/Seeing.Agent.WebUI/Services/WorkspaceGate.cs
namespace Seeing.Agent.WebUI.Services;

/// <summary>未选定工作区时把任何业务路由压回首页。</summary>
public static class WorkspaceGate
{
    public const string HomePath = "/workspaces";

    public static bool ShouldForceWorkspaceHome(bool isResolved, string relativePath)
    {
        if (isResolved)
            return false;

        // 传入的是 ToBaseRelativePath 的结果（无前导斜杠，可含 query/hash）。
        var path = relativePath.Split('?', '#')[0].Trim('/');
        return !string.Equals(path, HomePath.TrimStart('/'), StringComparison.OrdinalIgnoreCase);
    }
}
```

`ModuleRouter.razor` 在 `ResolveCurrent` 之前加守卫：

```csharp
@inject IWorkspaceProvider Workspace
```

```csharp
    private void ResolveCurrent()
    {
        var relative = Navigation.ToBaseRelativePath(Navigation.Uri);

        if (WorkspaceGate.ShouldForceWorkspaceHome(Workspace.IsResolved, relative))
        {
            Navigation.NavigateTo(WorkspaceGate.HomePath, forceLoad: false);
            _resolution = new ModuleRouteResolution(ModuleRouteKind.Fallback);
            return;
        }

        _locationKey = ModuleRouteResolver.NormalizePath(relative);
        _resolution = ModuleRouteResolver.Resolve(_locationKey, Registry, Catalog);
    }
```

`CoreShellUiContribution` 增加一条不进侧栏的路由：

```csharp
        new RouteContribution("/workspaces", "选择工作区", typeof(WorkspacesPage)),
```

- [ ] **Step 3: 写首页**

```razor
@page "/workspaces"
@using AntDesign
@using Seeing.Agent.Configuration
@using Seeing.Agent.Hosting.Execution
@using Seeing.Agent.WebUI.Services
@using Seeing.Agent.WebUI.Shared
@inject IWorkspaceProvider Workspace
@inject IWorkspaceHistoryStore History
@inject IWorkspaceSwitcher Switcher
@inject NavigationManager Navigation
@inject MessageService Messages
@layout EmptyLayout

<PageTitle>选择工作区 - Seeing</PageTitle>

<div class="hc">
    <header class="hc-brand">
        <img src="seeing-mark-64.png" alt="Seeing" class="hc-brand-mark" />
        <div class="hc-brand-rule"></div>
        <div class="hc-brand-copy">
            <div class="hc-brand-tagline">先选一个工作区</div>
            <div class="hc-brand-desc">选定后进入控制台，随时可在侧栏切换</div>
        </div>
    </header>

    @if (_error is not null)
    {
        <Alert Type="@AlertType.Error" Message="@_error" ShowIcon="true" />
    }

    <div class="ws-cards">
        @foreach (var card in _cards)
        {
            <div class="ws-card @(card.IsDefault ? "is-default" : null)"
                 @onclick="@(() => EnterAsync(card.Path))">
                <div class="ws-card-name">@card.Name</div>
                @if (card.IsDefault)
                {
                    <span class="ws-card-badge">默认</span>
                }
                <div class="ws-card-path">@card.Path</div>
            </div>
        }

        <div class="ws-card ws-card-new" @onclick="OpenPicker">
            <Icon Type="@IconType.Outline.FolderAdd" />
            <div class="ws-card-name">选择文件夹</div>
        </div>
    </div>

    @if (_cards.Count == 0)
    {
        <Empty Description="还没有工作区，选一个文件夹开始" ImageSize="96" />
    }
</div>

<DirectoryPicker @ref="_picker"
                 Title="选择工作区"
                 InitialPath="@(Workspace.IsResolved ? Workspace.ResolvedRoot : null)"
                 OnSelected="EnterAsync" />

@code {
    private record WorkspaceCard(string Name, string Path, bool IsDefault);

    private List<WorkspaceCard> _cards = new();
    private DirectoryPicker? _picker;
    private string? _error;
    private bool _busy;

    protected override void OnInitialized() => Reload();

    private void Reload()
    {
        var snapshot = History.Read();
        var recent = snapshot.Recent.Where(Directory.Exists).ToList();

        _cards = recent
            .Select(p => new WorkspaceCard(
                new DirectoryInfo(p).Name, p,
                string.Equals(p, snapshot.LastInteractive, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    private void OpenPicker() => _picker?.Show();

    private async Task EnterAsync(string path)
    {
        if (_busy)
            return;

        _busy = true;
        _error = null;
        StateHasChanged();

        try
        {
            var result = await Switcher.ApplyAsync(new WorkspaceApplyRequest(path));
            if (!result.Success)
            {
                _error = result.Error;
                return;
            }

            Navigation.NavigateTo("/", forceLoad: true);
        }
        finally
        {
            _busy = false;
            StateHasChanged();
        }
    }
}
```

`EmptyLayout` 需要新建（无侧栏）：

```razor
@inherits LayoutComponentBase
@using AntDesign

<Layout Style="min-height: 100vh;">
    <Content Class="content-area">
        @Body
    </Content>
</Layout>
```

放在 `samples/Seeing.Agent.WebUI/Shared/EmptyLayout.razor`。

- [ ] **Step 4: 构建并测试**

```bash
dotnet build samples/Seeing.Agent.WebUI
dotnet build tests/apps/Seeing.Agent.WebUI.Tests
dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll --TestCaseFilter:"FullyQualifiedName~WorkspaceGateTests"
```
Expected: 6 passed。

- [ ] **Step 5: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Pages/WorkspacesPage.razor samples/Seeing.Agent.WebUI/Shared/EmptyLayout.razor samples/Seeing.Agent.WebUI/Services/WorkspaceGate.cs samples/Seeing.Agent.WebUI/Components/ModuleRouter.razor samples/Seeing.Agent.WebUI/Services/CoreShellUiContribution.cs samples/Seeing.Agent.WebUI/wwwroot/css/workspace-page.css tests/apps/Seeing.Agent.WebUI.Tests/Services/WorkspaceGateTests.cs
git commit -m "feat(webui): 工作区首页与未决议路由守卫"
```

---

### Task 13: 侧栏切换入口

**Files:**
- Create: `samples/Seeing.Agent.WebUI/Components/WorkspaceSwitcherButton.razor`
- Modify: `samples/Seeing.Agent.WebUI/Components/AppSidebar.razor`（放进 footer 上方）
- Modify: `samples/Seeing.Agent.WebUI/Services/WorkspaceSwitchService.cs`

**Interfaces:**
- Consumes: `IWorkspaceSwitcher`、`IWorkspaceHistoryStore`、`IDirectoryBrowser`
- Produces: `WorkspaceSwitchService.ApplyAsync(string path)` 转调 `IWorkspaceSwitcher`

- [ ] **Step 1: 把 WorkspaceSwitchService 收窄成转发**

```csharp
// samples/Seeing.Agent.WebUI/Services/WorkspaceSwitchService.cs
using Seeing.Agent.Configuration;
using Seeing.Agent.Hosting.Execution;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 工作区切换的界面侧入口：转发到 <see cref="IWorkspaceSwitcher"/>，不再自行实现重载。
/// </summary>
public sealed class WorkspaceSwitchService
{
    private readonly IWorkspaceProvider _workspace;
    private readonly IWorkspaceSwitcher _switcher;

    public WorkspaceSwitchService(IWorkspaceProvider workspace, IWorkspaceSwitcher switcher)
    {
        _workspace = workspace;
        _switcher = switcher;
    }

    public Task<WorkspaceApplyResult> ApplyAsync(string path, CancellationToken ct = default) =>
        _switcher.ApplyAsync(new WorkspaceApplyRequest(path), ct);

    public string CurrentWorkspace => _workspace.IsResolved ? _workspace.ResolvedRoot! : "(未选定)";

    public string StartupDirectory => _workspace.StartupDirectory;

    public WorkspaceResolutionSource ResolutionSource => _workspace.ResolutionSource;

    public string ResolutionSourceDescription => _workspace.ResolutionSource switch
    {
        WorkspaceResolutionSource.EnvironmentVariable => "环境变量",
        WorkspaceResolutionSource.ProjectCustomPath => "项目自定义路径",
        WorkspaceResolutionSource.GlobalDefault => "上次在界面里选定",
        WorkspaceResolutionSource.StartupDirectory => "启动目录",
        WorkspaceResolutionSource.ManualSwitch => "本次切换",
        _ => "未知"
    };
}
```

- [ ] **Step 2: 写侧栏入口组件**

```razor
@using AntDesign
@using Seeing.Agent.Configuration
@using Seeing.Agent.Hosting.Execution
@using Seeing.Agent.WebUI.Services
@inject IWorkspaceProvider Workspace
@inject IWorkspaceHistoryStore History
@inject IWorkspaceSwitcher Switcher
@inject NavigationManager Navigation
@inject IMessageService Messages

<div class="ws-switcher">
    <Dropdown Trigger="@new[] { "click" }" Placement="@Placement.TopLeft">
        <Overlay>
            <Menu Mode="@MenuMode.Vertical">
                @foreach (var item in _recent)
                {
                    <MenuItem Key="@item" OnClick="@(() => SwitchAsync(item))">
                        <Icon Type="@(IsCurrent(item) ? IconType.Outline.Check : IconType.Outline.Folder)" />
                        @Name(item)
                    </MenuItem>
                }
                <MenuItem Key="__pick" OnClick="OpenPicker">
                    <Icon Type="@IconType.Outline.FolderAdd" />
                    选择文件夹…
                </MenuItem>
            </Menu>
        </Overlay>
        <ChildContent>
            <Button Type="@ButtonType.Text" Class="ws-switcher-button" Block="true">
                <Icon Type="@IconType.Outline.FolderOpen" />
                @if (!Collapsed)
                {
                    <span class="ws-switcher-label">@CurrentName</span>
                }
            </Button>
        </ChildContent>
    </Dropdown>
</div>

<DirectoryPicker @ref="_picker"
                 Title="切换工作区"
                 InitialPath="@(_recent.FirstOrDefault())"
                 OnSelected="SwitchAsync" />

@code {
    [Parameter] public bool Collapsed { get; set; }

    private DirectoryPicker? _picker;
    private IReadOnlyList<string> _recent = Array.Empty<string>();
    private bool _busy;

    private string CurrentName =>
        Workspace.IsResolved ? new DirectoryInfo(Workspace.ResolvedRoot!).Name : "未选定";

    protected override void OnInitialized() => Refresh();

    private void Refresh() =>
        _recent = History.Read().Recent.Where(Directory.Exists).Take(8).ToList();

    private bool IsCurrent(string path) =>
        Workspace.IsResolved &&
        string.Equals(Workspace.ResolvedRoot, path, StringComparison.OrdinalIgnoreCase);

    private static string Name(string path) => new DirectoryInfo(path).Name;

    private void OpenPicker()
    {
        Refresh();
        _picker?.Show();
    }

    private async Task SwitchAsync(string path)
    {
        if (_busy || IsCurrent(path))
            return;

        _busy = true;
        try
        {
            var result = await Switcher.ApplyAsync(new WorkspaceApplyRequest(path));
            if (!result.Success)
            {
                Messages.Error($"切换失败: {result.Error}");
                return;
            }

            Navigation.NavigateTo("/", forceLoad: true);
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }
}
```

在 `AppSidebar.razor` 的 footer 之前插入：

```razor
    <WorkspaceSwitcherButton Collapsed="@EffectiveCollapsed" />
```

- [ ] **Step 3: 构建**

```bash
dotnet build samples/Seeing.Agent.WebUI
```
Expected: 0 error。

- [ ] **Step 4: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Components/WorkspaceSwitcherButton.razor samples/Seeing.Agent.WebUI/Components/AppSidebar.razor samples/Seeing.Agent.WebUI/Services/WorkspaceSwitchService.cs
git commit -m "feat(webui): 侧栏工作区切换入口"
```

---

### Task 14: 清理设置页旧入口

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Pages/Settings.razor:115-170, 203-212, 225-300`
- Delete: `samples/Seeing.Agent.WebUI/Components/WorkspaceSelectorModal.razor`

- [ ] **Step 1: 删掉旧控件**

删除 `Settings.razor` 中：

- 「当前工作区」里的「切换」按钮（保留只读 `Input`，说明改成「在侧栏切换」）
- 「启动目录」`FormItem`
- 「工作区模式」`RadioGroup`
- 「全局默认工作区」`FormItem`
- 「自定义路径（可选）」`FormItem`
- 三个 `<WorkspaceSelectorModal .../>` 实例及对应字段 `_workspaceSelectorModal` / `_globalWorkspaceSelectorModal` / `_customWorkspaceSelectorModal` 与 `ShowWorkspaceSelector` / `ShowGlobalWorkspaceSelector` / `ShowCustomWorkspaceSelector` 方法

保留「工作区文件硬边界」开关。

- [ ] **Step 2: 删除旧组件**

```bash
git rm samples/Seeing.Agent.WebUI/Components/WorkspaceSelectorModal.razor
```

- [ ] **Step 3: 构建确认没有残留引用**

```bash
rg "WorkspaceSelectorModal|_workspaceUseGlobal|_customWorkspacePath|_globalWorkspacePath" samples/Seeing.Agent.WebUI
dotnet build samples/Seeing.Agent.WebUI
```
Expected: `rg` 无输出；构建 0 error。

- [ ] **Step 4: 提交**

```bash
git add -A samples/Seeing.Agent.WebUI
git commit -m "refactor(webui): 移除设置页里旧的工作区切换入口"
```

---

### Task 15: 各宿主登记启动请求

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Hosting/WebUiHost.cs`
- Modify: `samples/Seeing.Agent.WebUI/Program.cs`
- Modify: `samples/Seeing.Agent.Desktop/Program.cs`
- Modify: `samples/Seeing.Agent.Tui/Program.cs`
- Modify: `samples/Seeing.Agent.Cli/Infrastructure/CliServiceBootstrap.cs`
- Modify: `samples/Seeing.Gateway.Server/Program.cs`
- Test: `tests/apps/Seeing.Agent.Desktop.Tests/DesktopWorkspaceTests.cs`（追加）

**Interfaces:**
- Consumes: `WorkspaceLaunchRequest`、`WorkspaceLaunchMode`
- Produces: `WebUiHost.BuildAsync(string[] args, string? contentRoot = null, Action<ILoggingBuilder>? configureLogging = null, string? startupDirectory = null)`

- [ ] **Step 1: 追加桌面测试**

```csharp
    [Fact]
    public void Apply_ShouldNotBeCalledBeforeStartupDirectoryIsCaptured()
    {
        // 契约：桌面在 Apply() 之前捕获原始目录；Apply 之后 GetCurrentDirectory 已改变，
        // 因此 WebUiHost 不得再靠 GetCurrentDirectory 决议工作区。
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "samples", "Seeing.Agent.Desktop", "Program.cs"));

        var captureIndex = source.IndexOf("captureStartupDirectory", StringComparison.Ordinal);
        var applyIndex = source.IndexOf("DesktopWorkspace.Apply()", StringComparison.Ordinal);

        captureIndex.Should().BeGreaterThan(-1, "桌面必须先捕获原始启动目录");
        applyIndex.Should().BeGreaterThan(-1);
        captureIndex.Should().BeLessThan(applyIndex, "捕获必须在改写当前目录之前");
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Seeing.Agent.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("找不到仓库根");
    }
```

- [ ] **Step 2: WebUiHost 接收并登记启动目录**

`BuildAsync` 增加尾参 `string? startupDirectory = null`，并在 `builder.Build()` 之前：

```csharp
        // 启动目录必须由调用方传入：桌面外壳在 Build 之前已经改过进程当前目录，
        // 这里再读 GetCurrentDirectory 会拿到 %LOCALAPPDATA%\Seeing 而不是用户真正的启动位置。
        var launchStartup = startupDirectory ?? Directory.GetCurrentDirectory();
        builder.Services.AddSingleton(new WorkspaceLaunchRequest(new WorkspaceLaunchInput(
            Mode: WorkspaceLaunchMode.Interactive,
            StartupDirectory: launchStartup,
            ExecutableDirectory: AppContext.BaseDirectory,
            ExplicitPath: ReadWorkspaceArgument(args),
            InheritedEnvironment: Environment.GetEnvironmentVariable("SEEING_WORKSPACE_ROOT"),
            History: WorkspaceHistorySnapshot.Empty)));
```

```csharp
    /// <summary>读取 <c>--workspace &lt;目录&gt;</c>。</summary>
    private static string? ReadWorkspaceArgument(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--workspace", StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }
```

同处把 `ReloadGatewayChannelRegistry(workspaceProvider.GetProjectRoot())` 改为只在已决议时调用：

```csharp
            if (workspaceProvider.IsResolved)
                sp.ReloadGatewayChannelRegistry(workspaceProvider.GetProjectRoot());
```

注册 `IDirectoryBrowser`：`builder.Services.AddSingleton<IDirectoryBrowser, DirectoryBrowser>();`

- [ ] **Step 3: 桌面传原始目录**

`samples/Seeing.Agent.Desktop/Program.cs` 的 `Main` 开头：

```csharp
        // 捕获用户真正的启动位置：Apply() 之后进程当前目录会变，
        // 而启动决议要用的正是「双击/终端里启动时」的目录。
        var captureStartupDirectory = Directory.GetCurrentDirectory();

        var startLocation = DesktopWorkspace.Apply();
```

`BuildAsync` 调用处补参数：

```csharp
                app = WebUiHost.BuildAsync(hostArgs, AppContext.BaseDirectory, logging =>
                    {
                        logging.ClearProviders();
                        logging.AddProvider(logProvider);
                    }, captureStartupDirectory)
```

- [ ] **Step 4: TUI 与 CLI 登记 Invocation**

TUI `Program.cs` 在 `builder.Build()` 之前：

```csharp
builder.Services.AddSingleton(new WorkspaceLaunchRequest(new WorkspaceLaunchInput(
    Mode: WorkspaceLaunchMode.Invocation,
    StartupDirectory: Directory.GetCurrentDirectory(),
    ExecutableDirectory: null,
    ExplicitPath: options.Workspace,
    InheritedEnvironment: null,
    History: WorkspaceHistorySnapshot.Empty)));
```

同时删除 `Environment.SetEnvironmentVariable("SEEING_WORKSPACE_ROOT", options.Workspace)`（位置参数已进决议）。

CLI 的 `CliServiceBootstrap` 在 `builder.Build()` 之前：

```csharp
builder.Services.AddSingleton(new WorkspaceLaunchRequest(new WorkspaceLaunchInput(
    Mode: WorkspaceLaunchMode.Invocation,
    StartupDirectory: CliWorkspace.Resolve(),
    ExecutableDirectory: null,
    ExplicitPath: null,
    InheritedEnvironment: null,
    History: WorkspaceHistorySnapshot.Empty)));
```

`CliWorkspace.Resolve()` 去掉环境变量分支，改为只返回 `Directory.GetCurrentDirectory()`，并更新 XML 注释说明工作区由启动决议决定。`StatusCommand` / `StopCommand` / `ConfigCommand` 里的 `ResolveWorkspaceRoot` 一并改为调用 `CliWorkspace.Resolve()`。

- [ ] **Step 5: Gateway.Server 登记 Service**

```csharp
builder.Services.AddSingleton(new WorkspaceLaunchRequest(new WorkspaceLaunchInput(
    Mode: WorkspaceLaunchMode.Service,
    StartupDirectory: Directory.GetCurrentDirectory(),
    ExecutableDirectory: null,
    ExplicitPath: ReadWorkspaceArgument(args),
    InheritedEnvironment: Environment.GetEnvironmentVariable("SEEING_WORKSPACE_ROOT"),
    History: WorkspaceHistorySnapshot.Empty)));
```

`ReadWorkspaceArgument` 复制同一实现对（`samples/Seeing.Gateway.Server/Program.cs` 内 `static string? ReadWorkspaceArgument(string[] args)`）。

- [ ] **Step 6: 构建与测试**

```bash
dotnet build
dotnet build tests/apps/Seeing.Agent.Desktop.Tests
dotnet vstest tests/apps/Seeing.Agent.Desktop.Tests/bin/Debug/net10.0/Seeing.Agent.Desktop.Tests.dll --TestCaseFilter:"FullyQualifiedName~DesktopWorkspaceTests"
```
Expected: 全部通过。

- [ ] **Step 7: 提交**

```bash
git add samples/Seeing.Agent.WebUI samples/Seeing.Agent.Desktop samples/Seeing.Agent.Tui samples/Seeing.Agent.Cli samples/Seeing.Gateway.Server tests/apps/Seeing.Agent.Desktop.Tests
git commit -m "feat(hosts): 各宿主在构建前登记启动决议输入"
```

---

### Task 16: 启动时按决议写历史

**Files:**
- Modify: `src/spine/Seeing.Agent.Core/Extensions/ServiceCollectionExtensions.cs`（`InitializeSeeingAsync`）
- Test: `tests/spine/Seeing.Agent.Tests/Configuration/InitializeSeeingHistoryTests.cs`

**Interfaces:**
- Consumes: `WorkspaceLaunchDecision.UpdateRecent` / `UpdateLastInteractive`
- Produces: 决议成功后按返回值写入历史

- [ ] **Step 1: 写测试**

```csharp
// tests/spine/Seeing.Agent.Tests/Configuration/InitializeSeeingHistoryTests.cs
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Extensions;
using Xunit;

namespace Seeing.Agent.Tests.Configuration;

public class InitializeSeeingHistoryTests
{
    [Fact]
    public async Task ExplicitWorkspace_ShouldUpdateRecentOnly()
    {
        var root = NewDir();
        var store = new RecordingHistory();
        var services = Build(store, new WorkspaceLaunchInput(
            WorkspaceLaunchMode.Interactive, root, root, ExplicitPath: root,
            InheritedEnvironment: null, WorkspaceHistorySnapshot.Empty));

        await using var sp = services.BuildServiceProvider();
        await sp.InitializeSeeingAsync(TestContext.Current.CancellationToken);

        store.Calls.Should().ContainSingle().Which.Should().Be((root, false));
    }

    [Fact]
    public async Task LastInteractiveHit_ShouldNotWriteHistory()
    {
        var root = NewDir();
        var store = new RecordingHistory();
        var services = Build(store, new WorkspaceLaunchInput(
            WorkspaceLaunchMode.Interactive, Path.Combine(Path.GetTempPath(), "unrelated-" + Guid.NewGuid()),
            null, null, null,
            new WorkspaceHistorySnapshot(new[] { root }, root)));

        await using var sp = services.BuildServiceProvider();
        await sp.InitializeSeeingAsync(TestContext.Current.CancellationToken);

        store.Calls.Should().BeEmpty("自动进入上次的工作区不算一次新的选择");
    }

    private static string NewDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "seeing-h-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static ServiceCollection Build(IWorkspaceHistoryStore store, WorkspaceLaunchInput input)
    {
        var services = new ServiceCollection();
        var registry = ConfigSectionRegistry.CreateWithSpine();
        services.AddSingleton<IConfigSectionRegistry>(registry);
        services.AddSingleton<ILoggerFactory>(LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning)));
        services.AddSingleton(store);
        services.AddSingleton(new WorkspaceLaunchRequest(input));
        services.AddSeeingCore(registry);
        return services;
    }

    private sealed class RecordingHistory : IWorkspaceHistoryStore
    {
        public List<(string Path, bool AsDefault)> Calls { get; } = new();
        public WorkspaceHistorySnapshot Read() => WorkspaceHistorySnapshot.Empty;
        public void RecordOpened(string path, bool asDefault) => Calls.Add((path, asDefault));
    }
}
```

- [ ] **Step 2: 实现在 InitializeSeeingAsync 里写历史**

在 Task 5 里决议成功后追加：

```csharp
                        if (decision.Outcome == WorkspaceLaunchOutcome.Resolved && decision.WorkspaceRoot is { } root)
                        {
                            workspaceProvider.SetInitialWorkspace(root, decision.Source);

                            if (decision.UpdateRecent || decision.UpdateLastInteractive)
                                history?.RecordOpened(root, decision.UpdateLastInteractive);

                            await configManager.LoadAsync(cancellationToken);
                        }
```

`history` 是 Task 5 里已经取到的 `services.GetService<IWorkspaceHistoryStore>()`。

- [ ] **Step 3: 运行测试**

```bash
dotnet build tests/spine/Seeing.Agent.Tests
dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~InitializeSeeingHistoryTests"
```
Expected: 2 passed。

- [ ] **Step 4: 提交**

```bash
git add src/spine/Seeing.Agent.Core/Extensions/ServiceCollectionExtensions.cs tests/spine/Seeing.Agent.Tests/Configuration/InitializeSeeingHistoryTests.cs
git commit -m "feat(core): 启动决议成功时按返回值写入历史"
```

---

### Task 17: 回归与文档

**Files:**
- Modify: `docs/architecture/03-configuration-lifecycle.md`（工作区启动规则一节）
- Modify: `AGENTS.md`（WHERE TO LOOK 增加工作区首页与目录选择）

- [ ] **Step 1: 跑受影响测试项目**

```bash
dotnet build
dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll
dotnet vstest tests/hosting/Seeing.Agent.Hosting.Tests/bin/Debug/net10.0/Seeing.Agent.Hosting.Tests.dll
dotnet vstest tests/gateway/Seeing.Agent.Gateway.Tests/bin/Debug/net10.0/Seeing.Agent.Gateway.Tests.dll
dotnet vstest tests/capabilities/Seeing.Agent.Scheduler.Tests/bin/Debug/net10.0/Seeing.Agent.Scheduler.Tests.dll
dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll
```
Expected: 全部通过。若有失败，先修再继续。

- [ ] **Step 2: 手工验证**

```bash
dotnet run --project samples/Seeing.Agent.WebUI
```
逐项确认：
1. 首次启动（历史为空）落在 `/workspaces`，卡片为空，只有「选择文件夹」。
2. 用目录选择进入一个空目录，整页跳到 `/`，控制台可见。
3. 停掉再启动：直接进入该目录，不再出现首页。
4. 从该目录的终端再启动一次 WebUI（另一个端口）：直接进入该目录。
5. 侧栏切换到一个新目录：整页进入 `/`，会话列表为空。
6. 在会话执行中点侧栏切换：出现确认框；取消后执行继续；确认后执行停止并切换。
7. 设置页不再有「工作区模式 / 全局默认 / 自定义路径 / 切换」。
8. `dotnet run --project samples/Seeing.Agent.WebUI -- --workspace E:\some\dir` 直接进入该目录。

- [ ] **Step 3: 更新文档**

`docs/architecture/03-configuration-lifecycle.md` 增加小节：

```markdown
### 工作区启动选择（2026-11）

启动工作区由 `WorkspaceLaunchPolicy` 决定，宿主在 `Host.Build` 之前登记
`WorkspaceLaunchRequest`，`InitializeSeeingAsync` 消费它。

- 有界面宿主：`--workspace` → 继承的 `SEEING_WORKSPACE_ROOT` → 启动目录命中最近列表 →
  `lastInteractive` → 未选定（显示 `/workspaces`）。
- CLI / TUI：启动目录（TUI 可用位置参数覆盖）。
- 单独 Gateway.Server：`--workspace` → `SEEING_WORKSPACE_ROOT` → 启动目录。

`Workspace.CustomPath`、`Workspace.UseGlobal`、`GlobalWorkspaceRoot` 不再参与启动。
未选定时项目路径不可读（`ProjectSeeingDirectory` 抛异常），项目装载整体跳过。

切换工作区只有一条路径：`IWorkspaceSwitcher.ApplyAsync`（拒绝未决审批 → 取消在途并等空闲 →
改根 → 重读项目配置 → 一轮显式重载 → 参与者按新根重启）。
```

- [ ] **Step 4: 提交**

```bash
git add docs/architecture/03-configuration-lifecycle.md AGENTS.md
git commit -m "docs(architecture): 工作区启动选择与切换路径"
```

---

## 自审结果

**规格覆盖：** §1 目标 → Task 12/13/15；§2 决策表 → Task 2/4/8；§3 启动矩阵 → Task 2；§3.6 未选定 → Task 4/5；§4 历史 → Task 3/16；§5 首页 → Task 12；§6 Apply → Task 8/9/13；§7 目录选择 → Task 10/11；§8 设置页 → Task 14；§9 错误处理 → Task 8 回滚 + Task 3 容错 + Task 10 错误返回；§10 测试 → 各任务内；§11 范围外 → 未实现。

**类型一致性：** `WorkspaceLaunchDecision` / `WorkspaceApplyRequest` / `WorkspaceApplyResult` / `WorkspaceHistorySnapshot` 在 Task 1、2、8、12、13、16 中用法一致。`IWorkspaceSwitcher.ApplyAsync` 只出现在 Task 8 定义、Task 12/13 调用。

**已知需实现者确认的细节：**
- `WorkspaceHistoryStore.AcquireCrossProcess` 的 `Mutex` 释放方式（Task 3 Step 4 已给替换写法）。
- `UnifiedConfigManager` 的 mock 改为 `IWorkspaceConfigReloader` 窄接口（Task 8 已说明取舍）。
- `PathBoundary` 所在命名空间以实际文件为准。
