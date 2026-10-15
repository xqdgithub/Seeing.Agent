# 会话文件浏览器（File Workspace / FileExplorer）Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **本计划按「子 Agent 并行快速实现」组织**：任务按 Wave 分波，Wave 内同 Track 的任务可并行；每个 Wave 结束有**关键审查门（Review Gate）**，其余任务由子 Agent 自主完成、不逐任务审查。

**Goal:** 在会话页支持类似 VSCode 的文件树 + 多标签查看/编辑，agent 改文件后自动刷新；文件能力与 UI 分离为可复用契约、能力包、公共组件库三层。

**Architecture:** 契约落 `Seeing.Agent.Abstractions`（`Files` + `ISessionWorkspaceResolver`）；纯文件能力（读写/变更订阅）落新能力包 `Seeing.Agent.Files`，**不感知会话事件**；公共组件落新 Razor 类库 `Seeing.Agent.WebUI.Files`；agent 变更桥接固定在 WebUI 宿主层。

**Tech Stack:** .NET 10 / C# 13、Blazor Server、AntDesign 1.6.2、BlazorMonaco 3.5.0、Markdig 1.3.2、xUnit v3、FluentAssertions、Moq。

**规格:** [`docs/superpowers/specs/2026-10-14-session-file-explorer-design.md`](../specs/2026-10-14-session-file-explorer-design.md)

**测试命令约定（重要）：**
- 能力包测试：`dotnet build tests/capabilities/Seeing.Agent.Files.Tests` 后 `dotnet vstest tests/capabilities/Seeing.Agent.Files.Tests/bin/Debug/net10.0/Seeing.Agent.Files.Tests.dll`
- Core 测试：`dotnet build tests/spine/Seeing.Agent.Tests` 后 `dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~<类名>"`
- 构建：`dotnet build Seeing.Agent.slnx`

---

## Wave 0 — 共享地基（**必须串行完成**，其余全部依赖它）

### Task 0.1: 文件契约（Abstractions/Files + ISessionWorkspaceResolver）

**Files:**
- Create: `src/abstractions/Seeing.Agent.Abstractions/Files/IFileWorkspaceFactory.cs`
- Create: `src/abstractions/Seeing.Agent.Abstractions/Files/IFileWorkspace.cs`
- Create: `src/abstractions/Seeing.Agent.Abstractions/Files/FileResourceId.cs`
- Create: `src/abstractions/Seeing.Agent.Abstractions/Files/FileRevision.cs`
- Create: `src/abstractions/Seeing.Agent.Abstractions/Files/FileEntry.cs`
- Create: `src/abstractions/Seeing.Agent.Abstractions/Files/FileReadResult.cs`
- Create: `src/abstractions/Seeing.Agent.Abstractions/Files/FileWriteResult.cs`
- Create: `src/abstractions/Seeing.Agent.Abstractions/Files/FileChangeEvent.cs`
- Create: `src/abstractions/Seeing.Agent.Abstractions/Files/FileEnums.cs`
- Create: `src/abstractions/Seeing.Agent.Abstractions/Permissions/ISessionWorkspaceResolver.cs`

- [ ] **Step 1: 写枚举与 DTO**

`src/abstractions/Seeing.Agent.Abstractions/Files/FileEnums.cs`:

```csharp
namespace Seeing.Agent.Abstractions.Files;

/// <summary>读取模式：Display=截断用于展示；Full=完整用于编辑。</summary>
public enum FileReadMode { Display, Full }

/// <summary>文件内容分类。</summary>
public enum FileContentKind { Text, Markdown, Image, Binary }

/// <summary>磁盘变更种类。</summary>
public enum FileChangeKind { Created, Modified, Deleted, Renamed }
```

`src/abstractions/Seeing.Agent.Abstractions/Files/FileResourceId.cs`:

```csharp
namespace Seeing.Agent.Abstractions.Files;

/// <summary>稳定文件资源标识：根哈希 + 相对路径（统一 '/' 分隔）。</summary>
/// <param name="RootId">有效根绝对路径规范化哈希。</param>
/// <param name="RelativePath">相对根的路径，统一 '/' 分隔。</param>
public readonly record struct FileResourceId(string RootId, string RelativePath)
{
    /// <inheritdoc />
    public override string ToString() => $"{RootId}:{RelativePath}";
}
```

`src/abstractions/Seeing.Agent.Abstractions/Files/FileRevision.cs`:

```csharp
namespace Seeing.Agent.Abstractions.Files;

/// <summary>文件修订号：全文 SHA256 + 大小 + 最后写入时间。</summary>
/// <param name="Hash">全文内容的 SHA256（十六进制小写）。</param>
/// <param name="Size">文件字节数。</param>
/// <param name="LastWriteUtc">最后写入时间（UTC）。</param>
public readonly record struct FileRevision(string Hash, long Size, DateTime LastWriteUtc)
{
    /// <summary>与另一修订号是否相等；null 视为不匹配。</summary>
    public bool Matches(FileRevision? other) =>
        other is { } o && o.Size == Size
        && string.Equals(o.Hash, Hash, StringComparison.Ordinal);
}
```

`src/abstractions/Seeing.Agent.Abstractions/Files/FileEntry.cs`:

```csharp
namespace Seeing.Agent.Abstractions.Files;

/// <summary>目录项（文件或子目录）。</summary>
/// <param name="Id">资源标识。</param>
/// <param name="Name">名称（不含父路径）。</param>
/// <param name="IsDirectory">是否目录。</param>
/// <param name="Size">文件字节数；目录为 null。</param>
/// <param name="LastWriteUtc">最后写入时间；目录为 null。</param>
public sealed record FileEntry(
    FileResourceId Id,
    string Name,
    bool IsDirectory,
    long? Size,
    DateTime? LastWriteUtc);
```

`src/abstractions/Seeing.Agent.Abstractions/Files/FileReadResult.cs`:

```csharp
namespace Seeing.Agent.Abstractions.Files;

/// <summary>结构化读取结果。</summary>
public sealed record FileReadResult(
    FileResourceId Id,
    FileRevision? Revision,
    string? Content,
    FileContentKind Kind,
    string MimeType,
    string? Encoding,
    bool IsEditable,
    long Size,
    int TotalLines,
    bool IsTruncated,
    bool IsSampled,
    FileReadMode Mode);
```

`src/abstractions/Seeing.Agent.Abstractions/Files/FileWriteResult.cs`:

```csharp
namespace Seeing.Agent.Abstractions.Files;

/// <summary>写入结果。Conflict=true 表示 expectedRevision 不匹配、未写入。</summary>
public sealed record FileWriteResult(
    FileResourceId Id,
    FileRevision? Revision,
    bool Conflict,
    FileRevision? CurrentRevision,
    string? Diff);
```

`src/abstractions/Seeing.Agent.Abstractions/Files/FileChangeEvent.cs`:

```csharp
namespace Seeing.Agent.Abstractions.Files;

/// <summary>磁盘变更事实（仅 OS watcher，无来源标注、无 diff）。</summary>
public sealed record FileChangeEvent(
    FileResourceId Id,
    FileChangeKind Kind,
    FileRevision? NewRevision,
    string? OldPath,
    DateTime Timestamp);
```

- [ ] **Step 2: 写两个接口**

`src/abstractions/Seeing.Agent.Abstractions/Files/IFileWorkspace.cs`:

```csharp
namespace Seeing.Agent.Abstractions.Files;

/// <summary>会话级文件工作区 — 读写 + 变更订阅，路径受 IWorkspacePathGate 约束。</summary>
public interface IFileWorkspace
{
    /// <summary>所属会话 id。</summary>
    string SessionId { get; }

    /// <summary>有效根绝对路径规范化哈希；跨会话同根一致。</summary>
    string RootId { get; }

    /// <summary>有效根绝对路径。</summary>
    string RootPath { get; }

    /// <summary>把绝对/相对路径转为资源标识（不做存在性检查）。</summary>
    FileResourceId GetResourceId(string path);

    /// <summary>把资源标识转回绝对路径。</summary>
    string GetAbsolutePath(FileResourceId id);

    /// <summary>获取单条目元信息。</summary>
    Task<FileEntry> StatAsync(string path, CancellationToken ct = default);

    /// <summary>列出单层目录条目。</summary>
    Task<IReadOnlyList<FileEntry>> ListAsync(string path, CancellationToken ct = default);

    /// <summary>结构化读取；Display 截断展示、Full 完整用于编辑。</summary>
    Task<FileReadResult> ReadAsync(string path, FileReadMode mode = FileReadMode.Display, CancellationToken ct = default);

    /// <summary>全量写入；expectedRevision 非空时做乐观并发检查。</summary>
    Task<FileWriteResult> WriteAsync(string path, string content, FileRevision? expectedRevision = null, CancellationToken ct = default);

    /// <summary>字符串替换写入（语义同 EditTool）。</summary>
    Task<FileWriteResult> EditAsync(string path, string oldString, string newString, bool replaceAll = false, FileRevision? expectedRevision = null, CancellationToken ct = default);

    /// <summary>删除文件或目录。</summary>
    Task DeleteAsync(string path, CancellationToken ct = default);

    /// <summary>按需生成统一行 diff（LCS + hunk header）：baselineContent → 当前磁盘。</summary>
    Task<string> GetDiffAsync(string path, string baselineContent, CancellationToken ct = default);

    /// <summary>订阅磁盘变更（仅 OS watcher）。</summary>
    IAsyncEnumerable<FileChangeEvent> SubscribeChangesAsync(CancellationToken ct = default);
}
```

`src/abstractions/Seeing.Agent.Abstractions/Files/IFileWorkspaceFactory.cs`:

```csharp
namespace Seeing.Agent.Abstractions.Files;

/// <summary>文件工作区工厂 — 按会话有效根解析隔离的 facade。</summary>
public interface IFileWorkspaceFactory
{
    /// <summary>
    /// 解析会话对应的文件工作区（异步，可回落 LoadAsync）。
    /// 有效根 = ISessionWorkspaceResolver.ResolveWorkingDirectory(sessionId) ?? IWorkspaceProvider.GetProjectRoot()。
    /// 同一 (sessionId, 有效根) 复用；有效根变化重建。
    /// </summary>
    Task<IFileWorkspace> ResolveAsync(string sessionId, CancellationToken ct = default);

    /// <summary>引用计数减一，归零回收并释放 watcher 引用；幂等。</summary>
    void Release(string sessionId);
}
```

`src/abstractions/Seeing.Agent.Abstractions/Permissions/ISessionWorkspaceResolver.cs`:

```csharp
namespace Seeing.Agent.Abstractions.Permissions;

/// <summary>解析会话有效工作目录（绝对路径）；会话不存在或无 cwd 返回 null。</summary>
public interface ISessionWorkspaceResolver
{
    /// <summary>返回会话有效工作目录，或 null。</summary>
    string? ResolveWorkingDirectory(string sessionId);
}
```

- [ ] **Step 3: 构建验证**

Run: `dotnet build src/abstractions/Seeing.Agent.Abstractions/Seeing.Agent.Abstractions.csproj`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add src/abstractions/Seeing.Agent.Abstractions/Files src/abstractions/Seeing.Agent.Abstractions/Permissions/ISessionWorkspaceResolver.cs
git commit -m "feat(files): add file workspace contracts and session workspace resolver port"
```

---

### Task 0.2: Core 端口实现 + 门闸支持会话有效根

**Files:**
- Create: `src/spine/Seeing.Agent.Core/Core/Permission/SessionWorkspaceResolver.cs`
- Modify: `src/spine/Seeing.Agent.Core/Core/Permission/WorkspacePathGate.cs:20-58`
- Modify: `src/spine/Seeing.Agent.Core/Extensions/ServiceCollectionExtensions.cs:669`
- Test: `tests/spine/Seeing.Agent.Tests/Permissions/SessionWorkspaceResolverTests.cs`
- Test: `tests/spine/Seeing.Agent.Tests/Permissions/WorkspacePathGateSessionRootTests.cs`

- [ ] **Step 1: 写端口实现**

`src/spine/Seeing.Agent.Core/Core/Permission/SessionWorkspaceResolver.cs`:

```csharp
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Session.Core;

namespace Seeing.Agent.Core.Permission;

/// <summary>基于 ISessionManager 内存会话数据解析会话有效工作目录。</summary>
public sealed class SessionWorkspaceResolver : ISessionWorkspaceResolver
{
    private readonly ISessionManager _sessions;

    /// <summary>注入会话管理器。</summary>
    public SessionWorkspaceResolver(ISessionManager sessions)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    }

    /// <inheritdoc />
    public string? ResolveWorkingDirectory(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return null;

        var cwd = _sessions.Get(sessionId)?.WorkingDirectory;
        if (string.IsNullOrWhiteSpace(cwd))
            return null;

        // 仅当目录真实存在时才作为边界输入（避免无效路径导致误放行/异常）
        try
        {
            return Directory.Exists(cwd) ? Path.GetFullPath(cwd) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return null;
        }
    }
}
```

> **注意**：`ISessionManager` 的命名空间是 `Seeing.Session.Core`（见 `ISessionManager.cs`），若编译报错请按实际命名空间调整 `using`。

- [ ] **Step 2: 修改 WorkspacePathGate**

`src/spine/Seeing.Agent.Core/Core/Permission/WorkspacePathGate.cs` — 替换整个类为：

```csharp
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Configuration;

namespace Seeing.Agent.Core.Permission;

/// <summary>
/// 工作区路径硬边界门闸：Restrict=false 时一律放行；否则校验
/// 全局根 ∪ 会话有效根 ∪ 会话白名单。
/// </summary>
public sealed class WorkspacePathGate : IWorkspacePathGate
{
    private readonly IWorkspaceProvider _workspace;
    private readonly IPermissionGrantStore _grantStore;
    private readonly IOptionsMonitor<SeeingAgentOptions> _options;
    private readonly ISessionWorkspaceResolver? _sessionWorkspace;

    /// <summary>
    /// 构造路径门闸，注入工作区提供者、授权存储、配置监听与会话工作目录解析器。
    /// </summary>
    public WorkspacePathGate(
        IWorkspaceProvider workspace,
        IPermissionGrantStore grantStore,
        IOptionsMonitor<SeeingAgentOptions> options,
        ISessionWorkspaceResolver? sessionWorkspace = null)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _grantStore = grantStore ?? throw new ArgumentNullException(nameof(grantStore));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _sessionWorkspace = sessionWorkspace;
    }

    /// <inheritdoc />
    public string? EnsureAllowed(string sessionId, string path)
    {
        if (!_options.CurrentValue.Workspace.RestrictToWorkspace)
            return null;

        if (string.IsNullOrEmpty(sessionId))
            return "缺少会话 ID，无法在硬边界模式下访问文件。请使用 add_workspace_path 或确保会话上下文完整。";

        if (string.IsNullOrWhiteSpace(path))
            return "路径为空，无法在硬边界模式下访问。";

        try
        {
            var full = Path.GetFullPath(path);
            var root = _workspace.GetProjectRoot();
            if (!string.IsNullOrEmpty(root) && PathSafety.IsPathWithinDirectory(full, root))
                return null;

            // 会话自身有效根（绝对化后）视为边界内；非扩权，不写白名单
            var sessionCwd = _sessionWorkspace?.ResolveWorkingDirectory(sessionId);
            if (!string.IsNullOrEmpty(sessionCwd)
                && PathSafety.IsPathWithinDirectory(full, sessionCwd))
                return null;

            if (_grantStore.ContainsSessionPath(sessionId, full))
                return null;

            return $"路径不在工作区允许集内: {full}。请批准权限扩权，或使用 add_workspace_path 将目录加入会话白名单。";
        }
        catch (Exception ex)
        {
            return $"无法校验工作区路径边界: {ex.Message}";
        }
    }
}
```

- [ ] **Step 3: 注册端口**

`src/spine/Seeing.Agent.Core/Extensions/ServiceCollectionExtensions.cs` — 在 `:669` 前插入：

```csharp
            // 会话有效工作目录解析端口（供 WorkspacePathGate 认会话根；非扩权）
            services.AddSingleton<ISessionWorkspaceResolver, SessionWorkspaceResolver>();
            services.AddSingleton<IWorkspacePathGate, WorkspacePathGate>();
```

（删除原有单独的 `services.AddSingleton<IWorkspacePathGate, WorkspacePathGate>();`，改为上面两行；`ISessionWorkspaceResolver` 的 using 为 `Seeing.Agent.Abstractions.Permissions`，`SessionWorkspaceResolver` 同命名空间 `Seeing.Agent.Core.Permission`。）

- [ ] **Step 4: 写失败测试**

`tests/spine/Seeing.Agent.Tests/Permissions/SessionWorkspaceResolverTests.cs`:

```csharp
using FluentAssertions;
using Moq;
using Seeing.Agent.Core.Permission;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.Permissions;

public class SessionWorkspaceResolverTests
{
    [Fact]
    public void ResolveWorkingDirectory_NoSession_ShouldReturnNull()
    {
        var sessions = new Mock<ISessionManager>();
        sessions.Setup(s => s.Get("missing")).Returns((SessionData?)null);
        var resolver = new SessionWorkspaceResolver(sessions.Object);

        resolver.ResolveWorkingDirectory("missing").Should().BeNull();
    }

    [Fact]
    public void ResolveWorkingDirectory_EmptyId_ShouldReturnNull()
    {
        var resolver = new SessionWorkspaceResolver(new Mock<ISessionManager>().Object);
        resolver.ResolveWorkingDirectory("").Should().BeNull();
    }

    [Fact]
    public void ResolveWorkingDirectory_NonExistentDir_ShouldReturnNull()
    {
        var session = new SessionData { Id = "s1", WorkingDirectory = Path.Combine(Path.GetTempPath(), "no-such-dir-xyz-123") };
        var sessions = new Mock<ISessionManager>();
        sessions.Setup(s => s.Get("s1")).Returns(session);
        var resolver = new SessionWorkspaceResolver(sessions.Object);

        resolver.ResolveWorkingDirectory("s1").Should().BeNull();
    }

    [Fact]
    public void ResolveWorkingDirectory_ExistingDir_ShouldReturnFullPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "seeing-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var session = new SessionData { Id = "s2", WorkingDirectory = dir };
            var sessions = new Mock<ISessionManager>();
            sessions.Setup(s => s.Get("s2")).Returns(session);
            var resolver = new SessionWorkspaceResolver(sessions.Object);

            resolver.ResolveWorkingDirectory("s2").Should().Be(Path.GetFullPath(dir));
        }
        finally { Directory.Delete(dir, true); }
    }
}
```

> 若 `SessionData` 属性为 `init` 且 `Id` 非必填，按实际构造调整。

- [ ] **Step 5: 运行测试验证失败/通过**

Run: `dotnet build tests/spine/Seeing.Agent.Tests && dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~SessionWorkspaceResolverTests"`
Expected: PASS（4 个）

- [ ] **Step 6: 写门闸测试**

`tests/spine/Seeing.Agent.Tests/Permissions/WorkspacePathGateSessionRootTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Permission;
using Xunit;

namespace Seeing.Agent.Tests.Permissions;

public class WorkspacePathGateSessionRootTests
{
    private static IOptionsMonitor<SeeingAgentOptions> RestrictOn()
    {
        var m = new Mock<IOptionsMonitor<SeeingAgentOptions>>();
        m.Setup(o => o.CurrentValue).Returns(new SeeingAgentOptions
        {
            Workspace = new WorkspaceOptions { RestrictToWorkspace = true }
        });
        return m.Object;
    }

    private static IWorkspaceProvider WorkspaceAt(string root)
    {
        var w = new Mock<IWorkspaceProvider>();
        w.SetupGet(x => x.ProjectSeeingDirectory).Returns(Path.Combine(root, ".seeing"));
        return w.Object;
    }

    [Fact]
    public void SessionRoot_InsideSessionCwd_ShouldAllow()
    {
        var global = Path.Combine(Path.GetTempPath(), "ws-global-" + Guid.NewGuid().ToString("N"));
        var sessionCwd = Path.Combine(Path.GetTempPath(), "ws-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(global);
        Directory.CreateDirectory(sessionCwd);
        try
        {
            var resolver = new Mock<ISessionWorkspaceResolver>();
            resolver.Setup(r => r.ResolveWorkingDirectory("s1")).Returns(sessionCwd);
            var gate = new WorkspacePathGate(WorkspaceAt(global), new Mock<IPermissionGrantStore>().Object, RestrictOn(), resolver.Object);

            var file = Path.Combine(sessionCwd, "a.txt");
            gate.EnsureAllowed("s1", file).Should().BeNull();
        }
        finally { Directory.Delete(global, true); Directory.Delete(sessionCwd, true); }
    }

    [Fact]
    public void OutsideBothRoots_NotWhitelisted_ShouldReject()
    {
        var global = Path.Combine(Path.GetTempPath(), "ws-global-" + Guid.NewGuid().ToString("N"));
        var sessionCwd = Path.Combine(Path.GetTempPath(), "ws-session-" + Guid.NewGuid().ToString("N"));
        var other = Path.Combine(Path.GetTempPath(), "ws-other-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(global); Directory.CreateDirectory(sessionCwd); Directory.CreateDirectory(other);
        try
        {
            var resolver = new Mock<ISessionWorkspaceResolver>();
            resolver.Setup(r => r.ResolveWorkingDirectory("s1")).Returns(sessionCwd);
            var gate = new WorkspacePathGate(WorkspaceAt(global), new Mock<IPermissionGrantStore>().Object, RestrictOn(), resolver.Object);

            gate.EnsureAllowed("s1", Path.Combine(other, "a.txt")).Should().NotBeNull();
        }
        finally { Directory.Delete(global, true); Directory.Delete(sessionCwd, true); Directory.Delete(other, true); }
    }
}
```

> `SeeingAgentOptions.Workspace` / `WorkspaceOptions` 的准确类型名请按 `src/spine/Seeing.Agent.Core/Configuration/` 实际类名调整。

- [ ] **Step 7: 运行测试**

Run: `dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~WorkspacePathGate"`
Expected: PASS（含既有门闸测试 + 新增 2 个）

- [ ] **Step 8: Commit**

```bash
git add src/spine/Seeing.Agent.Core/Core/Permission/SessionWorkspaceResolver.cs src/spine/Seeing.Agent.Core/Core/Permission/WorkspacePathGate.cs src/spine/Seeing.Agent.Core/Extensions/ServiceCollectionExtensions.cs tests/spine/Seeing.Agent.Tests/Permissions
git commit -m "feat(core): session workspace resolver port + gate recognizes session root"
```

---

### Task 0.3: Tools.Support 纯函数上提 + LineDiff

**Files:**
- Create: `src/capabilities/Seeing.Agent.Tools.Support/FileContentSupport.cs`
- Create: `src/capabilities/Seeing.Agent.Tools.Support/LineDiff.cs`
- Test: `tests/capabilities/Seeing.Agent.Tools.Support.Tests/Seeing.Agent.Tools.Support.Tests.csproj`（若不存在则新建）
- Test: `tests/capabilities/Seeing.Agent.Tools.Support.Tests/LineDiffTests.cs`

> **先检查** `tests/capabilities/Seeing.Agent.Tools.Support.Tests` 是否已存在；不存在则新建 csproj（仿 `Seeing.Agent.Tools.FileSystem.Tests.csproj`，仅引用 `Seeing.Agent.Tools.Support`）。

- [ ] **Step 1: 写 LineDiff 失败测试**

`tests/capabilities/Seeing.Agent.Tools.Support.Tests/LineDiffTests.cs`:

```csharp
using FluentAssertions;
using Seeing.Agent.Tools.Support;
using Xunit;

namespace Seeing.Agent.Tools.Support.Tests;

public class LineDiffTests
{
    [Fact]
    public void Identical_ShouldReturnEmpty()
    {
        LineDiff.ComputeUnified("a\nb\n", "a\nb\n").Should().BeEmpty();
    }

    [Fact]
    public void SingleLineChange_ShouldHaveHunkHeader()
    {
        var diff = LineDiff.ComputeUnified("a\nb\nc\n", "a\nB\nc\n");
        diff.Should().Contain("@@");
        diff.Should().Contain("-b");
        diff.Should().Contain("+B");
    }

    [Fact]
    public void InsertedLine_ShouldNotMarkFollowingLinesChanged()
    {
        var diff = LineDiff.ComputeUnified("a\nc\n", "a\nb\nc\n");
        diff.Should().Contain("+b");
        diff.Should().NotContain("-c");
    }
}
```

- [ ] **Step 2: 运行测试验证失败**

Run: `dotnet build tests/capabilities/Seeing.Agent.Tools.Support.Tests && dotnet vstest tests/capabilities/Seeing.Agent.Tools.Support.Tests/bin/Debug/net10.0/Seeing.Agent.Tools.Support.Tests.dll --TestCaseFilter:"FullyQualifiedName~LineDiffTests"`
Expected: FAIL（`LineDiff` 未定义）

- [ ] **Step 3: 实现 LineDiff（LCS + hunk header）**

`src/capabilities/Seeing.Agent.Tools.Support/LineDiff.cs`:

```csharp
using System.Text;

namespace Seeing.Agent.Tools.Support;

/// <summary>统一行 diff（LCS 行级 + hunk header）。不依赖任何能力包。</summary>
public static class LineDiff
{
    /// <summary>生成 old→new 的统一 diff；无差异返回空串。</summary>
    public static string ComputeUnified(string oldText, string newText, int context = 3)
    {
        var oldLines = SplitLines(oldText);
        var newLines = SplitLines(newText);
        var ops = BuildOps(oldLines, newLines);
        if (ops.All(o => o.Kind == OpKind.Equal))
            return string.Empty;

        var sb = new StringBuilder();
        sb.Append("--- a\n+++ b\n");

        // 把 ops 按 context 聚成 hunk
        var changed = ops.Select((o, i) => (o, i)).Where(t => t.o.Kind != OpKind.Equal).Select(t => t.i).ToList();
        var i2 = 0;
        while (i2 < changed.Count)
        {
            var start = Math.Max(0, changed[i2] - context);
            var end = changed[i2];
            // 向后合并相邻 hunk
            while (i2 + 1 < changed.Count && changed[i2 + 1] <= end + context * 2 + 1)
            {
                i2++;
                end = changed[i2];
            }
            end = Math.Min(ops.Count - 1, end + context);
            AppendHunk(sb, ops, start, end);
            i2++;
        }
        return sb.ToString();
    }

    private enum OpKind { Equal, Delete, Insert }
    private readonly record struct Op(OpKind Kind, string Line);

    private static List<string> SplitLines(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace("\r", "\n");
        if (normalized.EndsWith('\n'))
            normalized = normalized[..^1];
        return normalized.Length == 0 ? new List<string>() : normalized.Split('\n').ToList();
    }

    private static List<Op> BuildOps(List<string> a, List<string> b)
    {
        var n = a.Count; var m = b.Count;
        var lcs = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var ops = new List<Op>();
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (a[x] == b[y]) { ops.Add(new Op(OpKind.Equal, a[x])); x++; y++; }
            else if (lcs[x + 1, y] >= lcs[x, y + 1]) { ops.Add(new Op(OpKind.Delete, a[x])); x++; }
            else { ops.Add(new Op(OpKind.Insert, b[y])); y++; }
        }
        while (x < n) { ops.Add(new Op(OpKind.Delete, a[x++])); }
        while (y < m) { ops.Add(new Op(OpKind.Insert, b[y++])); }
        return ops;
    }

    private static void AppendHunk(StringBuilder sb, List<Op> ops, int start, int end)
    {
        var oldStart = 1; var newStart = 1;
        for (var k = 0; k < start; k++)
        {
            if (ops[k].Kind != OpKind.Insert) oldStart++;
            if (ops[k].Kind != OpKind.Delete) newStart++;
        }
        var oldCount = 0; var newCount = 0;
        for (var k = start; k <= end; k++)
        {
            if (ops[k].Kind != OpKind.Insert) oldCount++;
            if (ops[k].Kind != OpKind.Delete) newCount++;
        }
        sb.Append($"@@ -{oldStart},{oldCount} +{newStart},{newCount} @@\n");
        for (var k = start; k <= end; k++)
        {
            var prefix = ops[k].Kind switch { OpKind.Equal => " ", OpKind.Delete => "-", _ => "+" };
            sb.Append(prefix).Append(ops[k].Line).Append('\n');
        }
    }
}
```

- [ ] **Step 4: 写 FileContentSupport 失败测试**

`tests/capabilities/Seeing.Agent.Tools.Support.Tests/FileContentSupportTests.cs`:

```csharp
using FluentAssertions;
using Seeing.Agent.Tools.Support;
using Xunit;

namespace Seeing.Agent.Tools.Support.Tests;

public class FileContentSupportTests
{
    [Theory]
    [InlineData("a.png", "image/png")]
    [InlineData("a.md", "text/markdown")]
    [InlineData("a.unknownext", "application/octet-stream")]
    public void GetMimeType_ShouldMap(string name, string expected)
        => FileContentSupport.GetMimeType(name).Should().Be(expected);

    [Theory]
    [InlineData("a.exe", true)]
    [InlineData("a.cs", false)]
    public void IsBinaryByExtension_ShouldDetect(string name, bool expected)
        => FileContentSupport.IsBinaryByExtension(name).Should().Be(expected);
}
```

- [ ] **Step 5: 实现 FileContentSupport（从 FileSystemHelper 上提的纯函数）**

`src/capabilities/Seeing.Agent.Tools.Support/FileContentSupport.cs`:

```csharp
namespace Seeing.Agent.Tools.Support;

/// <summary>文件内容纯函数支持：读取限制、MIME、二进制判定。从 Tools.FileSystem.FileSystemHelper 上提。</summary>
public static class FileContentSupport
{
    /// <summary>默认读取行数限制。</summary>
    public const int DefaultReadLimit = 2000;

    /// <summary>单行最大长度。</summary>
    public const int MaxLineLength = 2000;

    /// <summary>最大字节限制 (50KB)。</summary>
    public const int MaxBytes = 50 * 1024;

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".tar", ".gz", ".exe", ".dll", ".so", ".class", ".jar", ".war", ".7z",
        ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".odp",
        ".bin", ".dat", ".obj", ".o", ".a", ".lib", ".wasm", ".pyc", ".pyo",
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp", ".tiff", ".tif",
        ".pdf", ".mp3", ".mp4", ".avi", ".mov", ".wav", ".flv", ".mkv"
    };

    private static readonly Dictionary<string, string> MimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif", [".bmp"] = "image/bmp", [".ico"] = "image/x-icon",
        [".webp"] = "image/webp", [".svg"] = "image/svg+xml", [".tiff"] = "image/tiff", [".tif"] = "image/tiff",
        [".pdf"] = "application/pdf",
        [".txt"] = "text/plain", [".json"] = "application/json", [".xml"] = "application/xml",
        [".html"] = "text/html", [".css"] = "text/css", [".js"] = "application/javascript",
        [".ts"] = "application/javascript", [".md"] = "text/markdown", [".cs"] = "text/x-csharp",
        [".py"] = "text/x-python", [".java"] = "text/x-java", [".go"] = "text/x-go",
        [".rs"] = "text/x-rust", [".c"] = "text/x-c", [".cpp"] = "text/x-c++",
        [".h"] = "text/x-c", [".hpp"] = "text/x-c++", [".sh"] = "text/x-shellscript",
        [".bash"] = "text/x-shellscript", [".yaml"] = "text/yaml", [".yml"] = "text/yaml",
        [".toml"] = "text/x-toml", [".ini"] = "text/x-ini", [".cfg"] = "text/x-ini", [".env"] = "text/plain"
    };

    /// <summary>按扩展名获取 MIME 类型。</summary>
    public static string GetMimeType(string filePath)
        => MimeTypes.TryGetValue(Path.GetExtension(filePath), out var mime) ? mime : "application/octet-stream";

    /// <summary>按扩展名判定是否二进制。</summary>
    public static bool IsBinaryByExtension(string filePath)
        => BinaryExtensions.Contains(Path.GetExtension(filePath));

    /// <summary>归一化换行符为 '\n'。</summary>
    public static string NormalizeLineEndings(string text)
        => text.Replace("\r\n", "\n").Replace("\r", "\n");
}
```

- [ ] **Step 6: 运行测试**

Run: `dotnet build tests/capabilities/Seeing.Agent.Tools.Support.Tests && dotnet vstest tests/capabilities/Seeing.Agent.Tools.Support.Tests/bin/Debug/net10.0/Seeing.Agent.Tools.Support.Tests.dll`
Expected: PASS（全部）

- [ ] **Step 7: Commit**

```bash
git add src/capabilities/Seeing.Agent.Tools.Support tests/capabilities/Seeing.Agent.Tools.Support.Tests Seeing.Agent.slnx
git commit -m "feat(tools.support): promote file content pure functions + add LineDiff"
```

> 若新建了测试项目，记得在 `Seeing.Agent.slnx` 的 `/tests/capabilities/` 下加 `<Project Path="..." />`。

---

### Task 0.4: 能力集登记 + slnx + 文档

**Files:**
- Modify: `src/spine/Seeing.Agent.Core/CapabilitySets/BuiltInCapabilitySets.cs`
- Modify: `Seeing.Agent.slnx`
- Modify: `docs/architecture/02-modules.md`

- [ ] **Step 1: 登记能力集**

`BuiltInCapabilitySets.cs`：
- `Code`（:29-42）数组内加 `"files.workspace",`（放在 `"filesystem",` 之后）。
- `Work`（:45-57）数组内加 `"files.workspace",`。
- `Full`（:76-101）数组内加 `"files.workspace",`。
- `SecureDisabled`（:150-156）加 `"files.workspace",`。

- [ ] **Step 2: 更新 slnx**

`Seeing.Agent.slnx`：
- `/src/capabilities/` 下加：`<Project Path="src/capabilities/Seeing.Agent.Files/Seeing.Agent.Files.csproj" />`
- `/samples/` 下加：`<Project Path="samples/Seeing.Agent.WebUI.Files/Seeing.Agent.WebUI.Files.csproj" />`
- `/tests/capabilities/` 下加：`<Project Path="tests/capabilities/Seeing.Agent.Files.Tests/Seeing.Agent.Files.Tests.csproj" />`

> 若 Task 0.3 新建了 `Seeing.Agent.Tools.Support.Tests`，同样在 `/tests/capabilities/` 加。

- [ ] **Step 3: 更新文档**

`docs/architecture/02-modules.md` §3 表加一行：

```
| `Seeing.Agent.Files` | `files.workspace` | 文件工作区（读写/变更订阅，供 UI/宿主复用） |
```

- [ ] **Step 4: 构建（此时新项目尚未创建，构建会失败——这是预期的；只验证 Core 编译）**

Run: `dotnet build src/spine/Seeing.Agent.Core/Seeing.Agent.Core.csproj`
Expected: Build succeeded

- [ ] **Step 5: Commit**

```bash
git add src/spine/Seeing.Agent.Core/CapabilitySets/BuiltInCapabilitySets.cs Seeing.Agent.slnx docs/architecture/02-modules.md
git commit -m "chore: register files.workspace capability set + solution + docs"
```

---

### 🔍 **REVIEW GATE #1**（关键审查点，必须人工/主 Agent 审查）

**审查内容：**
1. `WorkspacePathGate` 改动是否**只**新增「会话有效根」放行，未改授权模型、未写白名单。
2. `ISessionWorkspaceResolver` 是否落 Abstractions、实现落 Core，无依赖环。
3. `LineDiff` 是否为真正的 LCS + hunk header（非现有劣质 `GenerateDiff`）。
4. `BuiltInCapabilitySets` 的 `Full`/`Code`/`Work`/`SecureDisabled` 是否都登记了 `files.workspace`。
5. 运行守门测试：
   Run: `dotnet build tests/spine/Seeing.Agent.Tests && dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~Full_Should_Enable_All_Implemented_Module_Ids"`
   Expected: PASS（注意：`files.workspace` 模块项目尚未创建，扫描器此时还发现不了它；此测试应在 Wave 1 Task 1.1 创建 csproj 后再跑一次确认）。

**通过后**方可并行启动 Wave 1。

---

## Wave 1 — 能力包 + 组件库（可**并行**，Track A 与 Track B 互不依赖）

### Track A：能力包 `Seeing.Agent.Files`

#### Task 1.1: 项目骨架 + FileWorkspaceFactory

**Files:**
- Create: `src/capabilities/Seeing.Agent.Files/Seeing.Agent.Files.csproj`
- Create: `src/capabilities/Seeing.Agent.Files/FileWorkspaceFactory.cs`
- Test: `tests/capabilities/Seeing.Agent.Files.Tests/Seeing.Agent.Files.Tests.csproj`

- [ ] **Step 1: 建 csproj**

`src/capabilities/Seeing.Agent.Files/Seeing.Agent.Files.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>Seeing.Agent.Files</RootNamespace>
    <Description>文件工作区能力包：读写 + OS 变更订阅（不依赖会话事件）</Description>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
    <ProjectReference Include="..\..\abstractions\Seeing.Agent.Abstractions\Seeing.Agent.Abstractions.csproj" />
    <ProjectReference Include="..\Seeing.Agent.Tools.Support\Seeing.Agent.Tools.Support.csproj" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="Seeing.Agent.Files.Tests" />
  </ItemGroup>
</Project>
```

`tests/capabilities/Seeing.Agent.Files.Tests/Seeing.Agent.Files.Tests.csproj`（仿 FileSystem.Tests）：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>Seeing.Agent.Files.Tests</RootNamespace>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
    <UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="xunit.runner.visualstudio">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <PackageReference Include="coverlet.collector">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <PackageReference Include="FluentAssertions" />
    <PackageReference Include="Moq" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\..\src\capabilities\Seeing.Agent.Files\Seeing.Agent.Files.csproj" />
    <ProjectReference Include="..\..\..\src\capabilities\Seeing.IO.Local\Seeing.IO.Local.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: 实现 RevisionCalculator / FileExceptions / FileContentClassifier**

`RevisionCalculator.cs`:

```csharp
using System.Security.Cryptography;
using Seeing.Agent.Abstractions.Files;

namespace Seeing.Agent.Files;

/// <summary>基于全文内容的修订号计算。</summary>
internal static class RevisionCalculator
{
    /// <summary>对字节内容计算 SHA256 修订号。</summary>
    public static FileRevision FromBytes(byte[] bytes, DateTime lastWriteUtc)
    {
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new FileRevision(hash, bytes.LongLength, lastWriteUtc);
    }
}
```

`FileExceptions.cs`:

```csharp
namespace Seeing.Agent.Files;

/// <summary>文件过大，无法作为可编辑全文加载。</summary>
public sealed class FileTooLargeException : Exception
{
    /// <summary>构造。</summary>
    public FileTooLargeException(string path, long size, long limit)
        : base($"文件过大（{size} 字节，上限 {limit}）：{path}") { }
}

/// <summary>文件编码不支持编辑写入。</summary>
public sealed class FileNotEditableException : Exception
{
    /// <summary>构造。</summary>
    public FileNotEditableException(string path, string reason)
        : base($"文件不可编辑（{reason}）：{path}") { }
}
```

`FileContentClassifier.cs`:

```csharp
using System.Text;
using Seeing.Agent.Abstractions.Files;
using Seeing.Agent.Tools.Support;

namespace Seeing.Agent.Files;

/// <summary>内容分类：Kind / MIME / 编码 / 可编辑性。</summary>
internal static class FileContentClassifier
{
    /// <summary>判定内容类型。</summary>
    public static FileContentKind Classify(string path, string mime)
    {
        if (mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && mime != "image/svg+xml")
            return FileContentKind.Image;
        if (FileContentSupport.IsBinaryByExtension(path))
            return FileContentKind.Binary;
        if (string.Equals(mime, "text/markdown", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetExtension(path), ".md", StringComparison.OrdinalIgnoreCase))
            return FileContentKind.Markdown;
        return FileContentKind.Text;
    }

    /// <summary>探测编码并判断是否可编辑（仅 UTF-8 / ASCII）。</summary>
    public static (string? encoding, bool editable) DetectEditable(byte[] bytes)
    {
        if (bytes.Length == 0)
            return ("utf-8", true);

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return ("utf-8", true);

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return ("utf-16le", false);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return ("utf-16be", false);

        // 严格 UTF-8 校验（拒绝非法序列 → 视为非 UTF-8）
        try
        {
            var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            strict.GetString(bytes);
            return ("utf-8", true);
        }
        catch (DecoderFallbackException)
        {
            return (null, false);
        }
    }
}
```

- [ ] **Step 3: 实现 FileWorkspaceFactory**

`FileWorkspaceFactory.cs`:

```csharp
using System.Collections.Concurrent;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Files;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Files.Watching;
using Seeing.Agent.Configuration;

namespace Seeing.Agent.Files;

/// <summary>按 (sessionId, 有效根) 解析并缓存文件工作区 facade。</summary>
public sealed class FileWorkspaceFactory : IFileWorkspaceFactory, IDisposable
{
    private readonly ISessionManagerLookup _sessions;
    private readonly IWorkspaceProvider _workspace;
    private readonly IFileSystem _fileSystem;
    private readonly IWorkspacePathGate _gate;
    private readonly FileWatcherPool _watchers;
    private readonly ConcurrentDictionary<string, Entry> _cache = new();

    private sealed record Entry(IFileWorkspace Workspace, string Root, int RefCount);

    /// <summary>注入依赖。</summary>
    public FileWorkspaceFactory(
        ISessionManagerLookup sessions,
        IWorkspaceProvider workspace,
        IFileSystem fileSystem,
        IWorkspacePathGate gate,
        FileWatcherPool watchers)
    {
        _sessions = sessions;
        _workspace = workspace;
        _fileSystem = fileSystem;
        _gate = gate;
        _watchers = watchers;
    }

    /// <inheritdoc />
    public Task<IFileWorkspace> ResolveAsync(string sessionId, CancellationToken ct = default)
    {
        var cwd = _sessions.ResolveWorkingDirectory(sessionId);
        var root = string.IsNullOrEmpty(cwd) ? _workspace.GetProjectRoot() : cwd;
        root = Path.GetFullPath(root);

        if (_cache.TryGetValue(sessionId, out var existing) && existing.Root == root)
        {
            _cache[sessionId] = existing with { RefCount = existing.RefCount + 1 };
            return Task.FromResult(existing.Workspace);
        }

        if (existing is not null)
            Release(sessionId);

        var workspace = new FileWorkspace(sessionId, root, _fileSystem, _gate, _watchers);
        _cache[sessionId] = new Entry(workspace, root, 1);
        return Task.FromResult<IFileWorkspace>(workspace);
    }

    /// <inheritdoc />
    public void Release(string sessionId)
    {
        if (!_cache.TryGetValue(sessionId, out var entry))
            return;
        var next = entry with { RefCount = entry.RefCount - 1 };
        if (next.RefCount <= 0)
        {
            _cache.TryRemove(sessionId, out _);
            _watchers.ReleaseRoot(entry.Root);
        }
        else
        {
            _cache[sessionId] = next;
        }
    }

    /// <inheritdoc />
    public void Dispose() => _watchers.Dispose();
}
```

> `ISessionManagerLookup`：为避免能力包引用 `ISessionManager`（在 `Seeing.Session`），新增一个 Abstractions 端口的薄封装。**若 `Seeing.Agent.Abstractions` 已可引用 `Seeing.Session`（原语层允许）**，则可直接注入 `ISessionManager`。实现见 Task 1.2 Step 1 的 `ISessionManagerLookup`（放 Abstractions，Core 实现）。若决定直接用 `ISessionManager`，把上面的 `_sessions.ResolveWorkingDirectory` 换成 `ISessionWorkspaceResolver.ResolveWorkingDirectory`。

> **更简方案（推荐）**：工厂直接注入 `ISessionWorkspaceResolver`（Abstractions 端口，已在 Wave 0 提供），不要 `ISessionManagerLookup`。把 `_sessions` 换成 `ISessionWorkspaceResolver`，`ResolveAsync` 首行改为 `var cwd = _resolver.ResolveWorkingDirectory(sessionId);`。**本计划后续以该简方案为准。**

- [ ] **Step 4: 构建**

Run: `dotnet build src/capabilities/Seeing.Agent.Files/Seeing.Agent.Files.csproj`
Expected: Build succeeded（FileWorkspace / FileWatcherPool 将在后续 Task 补齐；本步允许因缺失类型失败，先建桩）

> **说明**：为让本 Task 可独立编译，先创建空壳 `FileWorkspace`/`FileWatcherPool`（Task 1.2/1.4 填充），或把 Task 1.1–1.5 合并为一个「能力包实现」任务串行做。**推荐合并**：Track A 由单个子 Agent 串行实现，避免桩文件返工。

- [ ] **Step 5: Commit**

```bash
git add src/capabilities/Seeing.Agent.Files tests/capabilities/Seeing.Agent.Files.Tests Seeing.Agent.slnx
git commit -m "feat(files): capability package skeleton + factory"
```

---

#### Task 1.2: FileWorkspace facade（读/写/编辑/diff）

**Files:**
- Create: `src/capabilities/Seeing.Agent.Files/FileWorkspace.cs`
- Create: `src/capabilities/Seeing.Agent.Files/PathWriteLocks.cs`
- Test: `tests/capabilities/Seeing.Agent.Files.Tests/FileWorkspaceTests.cs`
- Test: `tests/capabilities/Seeing.Agent.Files.Tests/PathWriteLocksTests.cs`

- [ ] **Step 1: 实现 PathWriteLocks**

```csharp
using System.Collections.Concurrent;

namespace Seeing.Agent.Files;

/// <summary>进程内按绝对路径写序列化（引用计数回收，best-effort）。</summary>
internal sealed class PathWriteLocks
{
    private sealed class Holder { public readonly SemaphoreSlim Sem = new(1, 1); public int RefCount; }

    private readonly ConcurrentDictionary<string, Holder> _locks = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _maxLocks;

    public PathWriteLocks(int maxLocks = 4096) => _maxLocks = maxLocks;

    /// <summary>获取路径锁；调用方负责在 using 结束时 Release。</summary>
    public async Task<IDisposable> AcquireAsync(string path, CancellationToken ct)
    {
        var holder = _locks.GetOrAdd(path, _ => new Holder());
        Interlocked.Increment(ref holder.RefCount);
        await holder.Sem.WaitAsync(ct).ConfigureAwait(false);
        return new Releaser(this, path, holder);
    }

    private void Release(string path, Holder holder)
    {
        holder.Sem.Release();
        if (Interlocked.Decrement(ref holder.RefCount) <= 0 && holder.Sem.CurrentCount == 1)
            _locks.TryRemove(path, out _);
    }

    private sealed class Releaser(PathWriteLocks owner, string path, Holder holder) : IDisposable
    {
        private bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            owner.Release(path, holder);
        }
    }
}
```

> `_maxLocks` 淘汰策略：本 Task 先实现引用计数回收；`MaxLocks` 淘汰可留 TODO（spec §5.3 的「最久未用淘汰」在 P1 之后增强）。

- [ ] **Step 2: 实现 FileWorkspace**

```csharp
using System.Runtime.CompilerServices;
using System.Text;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Files;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Files.Watching;
using Seeing.Agent.Tools.Support;

namespace Seeing.Agent.Files;

/// <summary>会话级文件工作区 facade。</summary>
internal sealed class FileWorkspace : IFileWorkspace
{
    private const long MaxReadBytes = 20 * 1024 * 1024;
    private const long MaxEditableSize = 5 * 1024 * 1024;
    private const long MaxHashableSize = 10 * 1024 * 1024;

    private readonly IFileSystem _fs;
    private readonly IWorkspacePathGate _gate;
    private readonly FileWatcherPool _watchers;
    private readonly PathWriteLocks _locks = new();

    public FileWorkspace(string sessionId, string rootPath, IFileSystem fs, IWorkspacePathGate gate, FileWatcherPool watchers)
    {
        SessionId = sessionId;
        RootPath = rootPath;
        RootId = ComputeRootId(rootPath);
        _fs = fs;
        _gate = gate;
        _watchers = watchers;
    }

    public string SessionId { get; }
    public string RootId { get; }
    public string RootPath { get; }

    private static string ComputeRootId(string root)
    {
        var normalized = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Replace('\\', '/');
        if (OperatingSystem.IsWindows()) normalized = normalized.ToLowerInvariant();
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant()[..16];
    }

    private void EnsureAllowed(string path)
    {
        var reason = _gate.EnsureAllowed(SessionId, path);
        if (reason is not null)
            throw new UnauthorizedAccessException(reason);
    }

    public FileResourceId GetResourceId(string path)
    {
        var full = Path.GetFullPath(path);
        var rel = Path.GetRelativePath(RootPath, full).Replace('\\', '/');
        return new FileResourceId(RootId, rel);
    }

    public string GetAbsolutePath(FileResourceId id) =>
        Path.GetFullPath(Path.Combine(RootPath, id.RelativePath.Replace('/', Path.DirectorySeparatorChar)));

    public Task<FileEntry> StatAsync(string path, CancellationToken ct = default)
    {
        var full = Path.GetFullPath(path);
        EnsureAllowed(full);
        var isDir = Directory.Exists(full);
        if (!isDir && !File.Exists(full))
            throw new FileNotFoundException("文件不存在", full);
        var size = isDir ? (long?)null : new FileInfo(full).Length;
        var lw = isDir ? (DateTime?)null : _fs.GetLastWriteTimeUtc(full);
        return Task.FromResult(new FileEntry(GetResourceId(full), Path.GetFileName(full), isDir, size, lw));
    }

    public Task<IReadOnlyList<FileEntry>> ListAsync(string path, CancellationToken ct = default)
    {
        var full = Path.GetFullPath(path);
        EnsureAllowed(full);
        if (!Directory.Exists(full))
            throw new FileNotFoundException("目录不存在", full);
        var list = new List<FileEntry>();
        foreach (var d in _fs.EnumerateDirectories(full, "*", false))
            list.Add(new FileEntry(GetResourceId(d), Path.GetFileName(d), true, null, null));
        foreach (var f in _fs.EnumerateFiles(full, "*", false))
            list.Add(new FileEntry(GetResourceId(f), Path.GetFileName(f), false, new FileInfo(f).Length, _fs.GetLastWriteTimeUtc(f)));
        return Task.FromResult<IReadOnlyList<FileEntry>>(list);
    }

    public async Task<FileReadResult> ReadAsync(string path, FileReadMode mode = FileReadMode.Display, CancellationToken ct = default)
    {
        var full = Path.GetFullPath(path);
        EnsureAllowed(full);
        var info = new FileInfo(full);
        if (!info.Exists) throw new FileNotFoundException("文件不存在", full);

        var mime = FileContentSupport.GetMimeType(full);
        var kind = FileContentClassifier.Classify(full, mime);

        byte[] bytes;
        var sampled = false;
        if (info.Length > MaxReadBytes)
        {
            bytes = await _fs.ReadAllBytesAsync(full, ct).ConfigureAwait(false);
            sampled = true;
        }
        else
        {
            bytes = await _fs.ReadAllBytesAsync(full, ct).ConfigureAwait(false);
        }

        var (encoding, editable) = FileContentClassifier.DetectEditable(bytes);
        FileRevision? revision = info.Length <= MaxHashableSize
            ? RevisionCalculator.FromBytes(bytes, info.LastWriteTimeUtc)
            : null;

        if (kind is FileContentKind.Binary or FileContentKind.Image)
        {
            return new FileReadResult(GetResourceId(full), revision, null, kind, mime, encoding,
                IsEditable: false, info.Length, 0, IsTruncated: false, IsSampled: sampled, mode);
        }

        var text = encoding == "utf-16le" ? Encoding.Unicode.GetString(bytes)
                 : encoding == "utf-16be" ? Encoding.BigEndianUnicode.GetString(bytes)
                 : new UTF8Encoding(false).GetString(bytes);

        var lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        var total = lines.Length;
        var truncated = false;
        if (mode == FileReadMode.Full && info.Length > MaxEditableSize)
            throw new FileTooLargeException(full, info.Length, MaxEditableSize);

        string content = text;
        if (mode == FileReadMode.Display && text.Length > FileContentSupport.MaxBytes)
        {
            content = text[..FileContentSupport.MaxBytes];
            truncated = true;
        }

        return new FileReadResult(GetResourceId(full), revision, content, kind, mime, encoding,
            editable && !truncated && info.Length <= MaxEditableSize, info.Length, total, truncated, sampled, mode);
    }

    public async Task<FileWriteResult> WriteAsync(string path, string content, FileRevision? expectedRevision = null, CancellationToken ct = default)
    {
        var full = Path.GetFullPath(path);
        EnsureAllowed(full);

        using var _ = await _locks.AcquireAsync(full, ct).ConfigureAwait(false);

        var exists = File.Exists(full);
        byte[]? oldBytes = exists ? await _fs.ReadAllBytesAsync(full, ct).ConfigureAwait(false) : null;
        var oldRev = exists ? RevisionCalculator.FromBytes(oldBytes!, _fs.GetLastWriteTimeUtc(full)) : (FileRevision?)null;

        if (expectedRevision is { } exp && !exp.Matches(oldRev))
            return new FileWriteResult(GetResourceId(full), null, Conflict: true, oldRev, null);

        if (exists)
        {
            var (_, editable) = FileContentClassifier.DetectEditable(oldBytes!);
            if (!editable)
                throw new FileNotEditableException(full, "非 UTF-8 编码");
        }

        await _fs.WriteAllTextAsync(full, content, ct).ConfigureAwait(false);
        var newBytes = Encoding.UTF8.GetBytes(content);
        var newRev = RevisionCalculator.FromBytes(newBytes, _fs.GetLastWriteTimeUtc(full));
        var oldText = oldBytes is null ? "" : new UTF8Encoding(false).GetString(oldBytes);
        var diff = LineDiff.ComputeUnified(oldText, content);
        return new FileWriteResult(GetResourceId(full), newRev, Conflict: false, null, diff);
    }

    public async Task<FileWriteResult> EditAsync(string path, string oldString, string newString, bool replaceAll = false, FileRevision? expectedRevision = null, CancellationToken ct = default)
    {
        var full = Path.GetFullPath(path);
        EnsureAllowed(full);
        if (!File.Exists(full)) throw new FileNotFoundException("文件不存在", full);
        var bytes = await _fs.ReadAllBytesAsync(full, ct).ConfigureAwait(false);
        var text = new UTF8Encoding(false).GetString(bytes);

        var index = text.IndexOf(oldString, StringComparison.Ordinal);
        if (index < 0) throw new InvalidOperationException("无法找到 oldString（需完全匹配）");
        string updated;
        if (replaceAll) updated = text.Replace(oldString, newString);
        else if (text.IndexOf(oldString, index + oldString.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException("存在多个匹配，请提供更多上下文或 replaceAll=true");
        else updated = text[..index] + newString + text[(index + oldString.Length)..];

        return await WriteAsync(full, updated, expectedRevision, ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string path, CancellationToken ct = default)
    {
        var full = Path.GetFullPath(path);
        EnsureAllowed(full);
        using var _ = await _locks.AcquireAsync(full, ct).ConfigureAwait(false);
        _fs.Delete(full);
    }

    public async Task<string> GetDiffAsync(string path, string baselineContent, CancellationToken ct = default)
    {
        var full = Path.GetFullPath(path);
        EnsureAllowed(full);
        var bytes = await _fs.ReadAllBytesAsync(full, ct).ConfigureAwait(false);
        var current = new UTF8Encoding(false).GetString(bytes);
        return LineDiff.ComputeUnified(baselineContent, current);
    }

    public async IAsyncEnumerable<FileChangeEvent> SubscribeChangesAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        using var sub = _watchers.Subscribe(RootPath);
        await foreach (var raw in sub.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            var full = Path.GetFullPath(raw.Path);
            if (!full.StartsWith(RootPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                continue;
            FileRevision? rev = null;
            if (raw.Kind is FileChangeKind.Modified or FileChangeKind.Created && File.Exists(full))
            {
                try { rev = RevisionCalculator.FromBytes(_fs.ReadAllBytes(full), _fs.GetLastWriteTimeUtc(full)); }
                catch { rev = null; }
            }
            yield return new FileChangeEvent(GetResourceId(full), raw.Kind, rev, raw.OldPath, DateTime.UtcNow);
        }
    }
}
```

- [ ] **Step 3: 写读写测试**

`FileWorkspaceTests.cs`（关键用例）：

```csharp
using FluentAssertions;
using Seeing.Agent.Abstractions.Files;
using Seeing.Agent.Files;
using Seeing.IO.Local;
using Xunit;

namespace Seeing.Agent.Files.Tests;

public class FileWorkspaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "files-test-" + Guid.NewGuid().ToString("N"));
    private readonly FileWorkspace _ws;

    public FileWorkspaceTests()
    {
        Directory.CreateDirectory(_root);
        var fs = new LocalFileSystem();
        var gate = new AllowAllGate();
        _ws = new FileWorkspace("s1", _root, fs, gate, new Watching.FileWatcherPool());
    }

    private sealed class AllowAllGate : Seeing.Agent.Abstractions.Permissions.IWorkspacePathGate
    {
        public string? EnsureAllowed(string sessionId, string path) => null;
    }

    [Fact]
    public async Task Write_Then_Read_Roundtrip()
    {
        var r = await _ws.WriteAsync("a.txt", "hello");
        r.Conflict.Should().BeFalse();
        var read = await _ws.ReadAsync("a.txt", FileReadMode.Full);
        read.Content.Should().Be("hello");
        read.IsEditable.Should().BeTrue();
    }

    [Fact]
    public async Task Write_StaleRevision_ShouldConflict()
    {
        await _ws.WriteAsync("a.txt", "v1");
        var stale = new FileRevision("deadbeef", 2, DateTime.UtcNow);
        var r = await _ws.WriteAsync("a.txt", "v2", stale);
        r.Conflict.Should().BeTrue();
        (await _ws.ReadAsync("a.txt", FileReadMode.Full)).Content.Should().Be("v1");
    }

    [Fact]
    public async Task Read_NonUtf8_ShouldNotBeEditable()
    {
        await File.WriteAllBytesAsync(Path.Combine(_root, "b.bin"), new byte[] { 0xFF, 0xFE, 0x41, 0x00 });
        var read = await _ws.ReadAsync("b.bin");
        read.IsEditable.Should().BeFalse();
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
```

> `FileWorkspace` 为 `internal`，测试项目已通过 `InternalsVisibleTo` 可见。`FileWatcherPool` 需提供公共无参构造（Task 1.4）。

- [ ] **Step 4: 运行测试**

Run: `dotnet build tests/capabilities/Seeing.Agent.Files.Tests && dotnet vstest tests/capabilities/Seeing.Agent.Files.Tests/bin/Debug/net10.0/Seeing.Agent.Files.Tests.dll`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/capabilities/Seeing.Agent.Files tests/capabilities/Seeing.Agent.Files.Tests
git commit -m "feat(files): FileWorkspace facade with read/write/edit/diff + path locks"
```

---

#### Task 1.3: Watching（FileWatcherPool + RootWatcher）

**Files:**
- Create: `src/capabilities/Seeing.Agent.Files/Watching/FileWatcherPool.cs`
- Create: `src/capabilities/Seeing.Agent.Files/Watching/RootWatcher.cs`
- Test: `tests/capabilities/Seeing.Agent.Files.Tests/FileWatcherPoolTests.cs`

- [ ] **Step 1: 实现 RootWatcher**

```csharp
using System.Collections.Concurrent;
using System.Threading.Channels;
using Seeing.Agent.Abstractions.Files;

namespace Seeing.Agent.Files.Watching;

/// <summary>单根 FileSystemWatcher 包装 + 防抖 + 稳定性校验。</summary>
internal sealed class RootWatcher : IDisposable
{
    /// <summary>归一化后的原始事件。</summary>
    public readonly record struct RawChange(string Path, FileChangeKind Kind, string? OldPath);

    private static readonly string[] Ignored = [".git", "bin", "obj", "node_modules", ".vs"];
    private readonly FileSystemWatcher _fsw;
    private readonly ConcurrentDictionary<string, DateTime> _pending = new();
    private readonly Timer _debounce;
    private readonly List<Channel<RawChange>> _sinks = [];
    private readonly object _sinkLock = new();

    public RootWatcher(string root)
    {
        _fsw = new FileSystemWatcher(root) { IncludeSubdirectories = true, EnableRaisingEvents = true };
        _fsw.Created += (_, e) => Queue(e.FullPath, FileChangeKind.Created, null);
        _fsw.Changed += (_, e) => Queue(e.FullPath, FileChangeKind.Modified, null);
        _fsw.Deleted += (_, e) => Queue(e.FullPath, FileChangeKind.Deleted, null);
        _fsw.Renamed += (_, e) => Queue(e.FullPath, FileChangeKind.Renamed, e.OldFullPath);
        _debounce = new Timer(_ => Flush(), null, 200, 200);
    }

    public ChannelReader<RawChange> Subscribe(ChannelWriter<RawChange> writer)
    {
        lock (_sinkLock) { var ch = Channel.CreateUnbounded<RawChange>(); _sinks.Add(ch); return ch.Reader; }
    }

    private bool IsIgnored(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(p => Ignored.Contains(p, StringComparer.OrdinalIgnoreCase));
    }

    private void Queue(string path, FileChangeKind kind, string? oldPath)
    {
        if (IsIgnored(path)) return;
        _pending[path + "|" + kind + "|" + (oldPath ?? "")] = DateTime.UtcNow;
    }

    private void Flush()
    {
        var now = DateTime.UtcNow;
        foreach (var (key, ts) in _pending.ToArray())
        {
            if ((now - ts).TotalMilliseconds < 200) continue;
            if (!_pending.TryRemove(key, out _)) continue;
            var parts = key.Split('|');
            var raw = new RawChange(parts[0], Enum.Parse<FileChangeKind>(parts[1]), parts[2].Length == 0 ? null : parts[2]);
            lock (_sinkLock)
                foreach (var ch in _sinks) ch.Writer.TryWrite(raw);
        }
    }

    public void Dispose()
    {
        _debounce.Dispose();
        _fsw.Dispose();
        lock (_sinkLock) { foreach (var ch in _sinks) ch.Writer.TryComplete(); _sinks.Clear(); }
    }
}
```

> **稳定性校验简化说明**：本 Task 先实现防抖（200ms）；spec §5.5 的 size/mtime 双采样可作增强。`Subscribe` 返回 `ChannelReader`，`FileWorkspace.SubscribeChangesAsync` 需相应调整（见 Task 1.2 Step 2 的 `sub.Reader.ReadAllAsync`）。

- [ ] **Step 2: 实现 FileWatcherPool**

```csharp
using System.Collections.Concurrent;

namespace Seeing.Agent.Files.Watching;

/// <summary>按根路径池化 RootWatcher + 引用计数。</summary>
public sealed class FileWatcherPool : IDisposable
{
    private sealed class Entry { public required RootWatcher Watcher; public int RefCount; }
    private readonly ConcurrentDictionary<string, Entry> _roots = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>按根订阅；调用方 Dispose 时 Release。</summary>
    internal RootWatcher.Subscription Subscribe(string root)
    {
        var normalized = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var entry = _roots.GetOrAdd(normalized, _ => new Entry { Watcher = new RootWatcher(normalized) });
        Interlocked.Increment(ref entry.RefCount);
        return new RootWatcher.Subscription(entry.Watcher, () => ReleaseRoot(normalized));
    }

    /// <summary>释放某根的一个引用；归零则 dispose watcher。</summary>
    public void ReleaseRoot(string root)
    {
        var normalized = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!_roots.TryGetValue(normalized, out var entry)) return;
        if (Interlocked.Decrement(ref entry.RefCount) <= 0 && _roots.TryRemove(normalized, out var removed))
            removed.Watcher.Dispose();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var (_, e) in _roots) e.Watcher.Dispose();
        _roots.Clear();
    }
}
```

> `RootWatcher.Subscription` 需在 `RootWatcher.cs` 内定义（含 `Reader` 属性 + `Dispose` 回调）。补：

```csharp
// 追加到 RootWatcher.cs 内
public sealed class Subscription : IDisposable
{
    private readonly Action _release;
    private bool _done;
    public ChannelReader<RawChange> Reader { get; }
    internal Subscription(RootWatcher owner, Action release)
    {
        var ch = Channel.CreateUnbounded<RawChange>();
        Reader = owner.Subscribe(ch.Writer);
        _release = release;
    }
    public void Dispose() { if (_done) return; _done = true; _release(); }
}
```

- [ ] **Step 3: 写池测试**

```csharp
using FluentAssertions;
using Seeing.Agent.Files.Watching;
using Xunit;

namespace Seeing.Agent.Files.Tests;

public class FileWatcherPoolTests
{
    [Fact]
    public void SameRoot_ShouldShareOneWatcher()
    {
        var root = Path.Combine(Path.GetTempPath(), "wpool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var pool = new FileWatcherPool();
            var a = pool.Subscribe(root);
            var b = pool.Subscribe(root);
            // 两个订阅指向同一 watcher 实例（通过引用计数间接验证：Release 一次不 dispose）
            a.Dispose();
            var c = pool.Subscribe(root); // 仍可用
            b.Dispose(); c.Dispose();
            pool.ReleaseRoot(root);
        }
        finally { Directory.Delete(root, true); }
    }
}
```

- [ ] **Step 4: 运行测试 + Commit**

Run: `dotnet build tests/capabilities/Seeing.Agent.Files.Tests && dotnet vstest tests/capabilities/Seeing.Agent.Files.Tests/bin/Debug/net10.0/Seeing.Agent.Files.Tests.dll --TestCaseFilter:"FullyQualifiedName~FileWatcherPoolTests"`

```bash
git add src/capabilities/Seeing.Agent.Files/Watching tests/capabilities/Seeing.Agent.Files.Tests
git commit -m "feat(files): watcher pool + root watcher with debounce"
```

---

#### Task 1.4: FileWorkspaceModule + FileWatcherHostedService

**Files:**
- Create: `src/capabilities/Seeing.Agent.Files/FileWorkspaceModule.cs`
- Create: `src/capabilities/Seeing.Agent.Files/Hosting/FileWatcherHostedService.cs`
- Test: `tests/capabilities/Seeing.Agent.Files.Tests/FileWorkspaceModuleTests.cs`

- [ ] **Step 1: 实现 HostedService**

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Files.Watching;

namespace Seeing.Agent.Files.Hosting;

/// <summary>随 Generic Host / 模块 Activate 启停文件 watcher 池。</summary>
public sealed class FileWatcherHostedService : IHostedService, IModuleHostedService
{
    /// <summary>模块 id。</summary>
    public const string ModuleIdValue = "files.workspace";

    private readonly FileWatcherPool _pool;
    private readonly IModuleCatalog? _catalog;
    private readonly ILogger<FileWatcherHostedService> _logger;
    private readonly ModuleHostedRunGate _run = new();

    /// <summary>注入。</summary>
    public FileWatcherHostedService(FileWatcherPool pool, ILogger<FileWatcherHostedService> logger, IModuleCatalog? catalog = null)
    {
        _pool = pool; _logger = logger; _catalog = catalog;
    }

    /// <inheritdoc />
    public string ModuleId => ModuleIdValue;

    /// <inheritdoc />
    public bool IsRunning => _run.IsRunning;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_run.IsRunning) return Task.CompletedTask;
        if (_catalog is not null && !_catalog.IsEnabled(ModuleIdValue))
        {
            _logger.LogDebug("files.workspace disabled; watcher hosted service no-op");
            return Task.CompletedTask;
        }
        _run.TryBegin();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _pool.Dispose();
        _run.End();
        return Task.CompletedTask;
    }
}
```

> `ModuleHostedRunGate` 在 `Seeing.Agent.Abstractions.Modules`（见 `ModuleHostedRunGate.cs`），若命名空间不同请调整 using。

- [ ] **Step 2: 实现 Module**

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Seeing.Agent.Abstractions.Files;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Files.Hosting;
using Seeing.Agent.Files.Watching;

namespace Seeing.Agent.Files;

/// <summary>文件工作区能力模块（无工具；提供 IFileWorkspaceFactory）。</summary>
public sealed class FileWorkspaceModule : ISeeingModule
{
    /// <inheritdoc />
    public string Id => "files.workspace";

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedTools { get; } = [];

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedSeams { get; } = [];

    /// <inheritdoc />
    public IReadOnlyList<string> DependsOn { get; } = ["io.local"];

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        services.TryAddSingleton<FileWatcherPool>();
        services.TryAddSingleton<IFileWorkspaceFactory, FileWorkspaceFactory>();
        services.AddModuleHostedService<FileWatcherHostedService>();
    }

    /// <inheritdoc />
    public Task ActivateAsync(IServiceProvider services, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task DeactivateAsync(IServiceProvider services, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
```

- [ ] **Step 3: 写模块测试（无参构造 + Id）**

```csharp
using FluentAssertions;
using Seeing.Agent.Files;
using Xunit;

namespace Seeing.Agent.Files.Tests;

public class FileWorkspaceModuleTests
{
    [Fact]
    public void Module_ShouldBeConstructible_AndDeclareId()
    {
        var m = new FileWorkspaceModule();
        m.Id.Should().Be("files.workspace");
        m.DependsOn.Should().Contain("io.local");
        m.ProvidedSeams.Should().BeEmpty();
    }
}
```

- [ ] **Step 4: 构建 + 运行守门测试**

Run: `dotnet build Seeing.Agent.slnx`（首次可能因 WebUI.Files 未建而失败；先只构建 `src/capabilities/Seeing.Agent.Files` 与测试）
Run: `dotnet build tests/spine/Seeing.Agent.Tests && dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~Full_Should_Enable_All_Implemented_Module_Ids"`
Expected: PASS（`files.workspace` 已被登记进 `Full`）

- [ ] **Step 5: Commit**

```bash
git add src/capabilities/Seeing.Agent.Files tests/capabilities/Seeing.Agent.Files.Tests
git commit -m "feat(files): module + watcher hosted service with IsEnabled gate"
```

---

### Track B：组件库 `Seeing.Agent.WebUI.Files`（与 Track A 并行）

#### Task 1.5: 项目骨架 + FileTabsState（纯 C#）

**Files:**
- Create: `samples/Seeing.Agent.WebUI.Files/Seeing.Agent.WebUI.Files.csproj`
- Create: `samples/Seeing.Agent.WebUI.Files/Services/FileTabsState.cs`
- Test: `tests/apps/Seeing.Agent.WebUI.Files.Tests/Seeing.Agent.WebUI.Files.Tests.csproj`
- Test: `tests/apps/Seeing.Agent.WebUI.Files.Tests/FileTabsStateTests.cs`

- [ ] **Step 1: 建 csproj（RCL，非 CPM）**

`samples/Seeing.Agent.WebUI.Files/Seeing.Agent.WebUI.Files.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Razor">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
    <RootNamespace>Seeing.Agent.WebUI.Files</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="AntDesign" Version="1.6.2" />
    <PackageReference Include="BlazorMonaco" Version="3.5.0" />
    <PackageReference Include="Markdig" Version="1.3.2" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\abstractions\Seeing.Agent.Abstractions\Seeing.Agent.Abstractions.csproj" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="Seeing.Agent.WebUI.Files.Tests" />
  </ItemGroup>
</Project>
```

`tests/apps/Seeing.Agent.WebUI.Files.Tests/Seeing.Agent.WebUI.Files.Tests.csproj`: 仿 `Seeing.Agent.WebUI.Tests.csproj`（xunit.v3 + FluentAssertions + Moq），引用 `samples/Seeing.Agent.WebUI.Files`。`ManagePackageVersionsCentrally=false`。

- [ ] **Step 2: 实现 FileTabsState**

```csharp
using Seeing.Agent.Abstractions.Files;

namespace Seeing.Agent.WebUI.Files.Services;

/// <summary>单个打开文件标签。</summary>
public sealed class OpenFile
{
    /// <summary>资源标识。</summary>
    public required FileResourceId Id { get; set; }
    /// <summary>当前编辑器内容（未保存时与磁盘不同）。</summary>
    public required string Content { get; set; }
    /// <summary>打开时的磁盘修订号。</summary>
    public FileRevision? OpenedRevision { get; set; }
    /// <summary>是否有未保存修改。</summary>
    public bool HasUnsavedChanges { get; set; }
    /// <summary>是否只读（截断/非 UTF-8/超大）。</summary>
    public bool IsReadOnly { get; set; }
    /// <summary>是否已从磁盘删除。</summary>
    public bool IsDeleted { get; set; }
    /// <summary>最近一次 agent/磁盘 diff（可选）。</summary>
    public string? PendingDiff { get; set; }
}

/// <summary>多标签状态（纯 C#，UI 无关）。</summary>
public sealed class FileTabsState
{
    private readonly List<OpenFile> _open = [];
    private readonly Dictionary<string, OpenFile> _byKey = new(StringComparer.Ordinal);

    /// <summary>标签集合变更事件。</summary>
    public event Action? Changed;

    /// <summary>已打开标签。</summary>
    public IReadOnlyList<OpenFile> OpenFiles => _open;

    /// <summary>当前激活标签键。</summary>
    public string? ActiveKey { get; private set; }

    private static string Key(FileResourceId id) => id.ToString();

    /// <summary>打开或激活文件。</summary>
    public void Open(FileReadResult file)
    {
        var key = Key(file.Id);
        if (_byKey.TryGetValue(key, out var existing))
        {
            ActiveKey = key;
        }
        else
        {
            var of = new OpenFile
            {
                Id = file.Id,
                Content = file.Content ?? "",
                OpenedRevision = file.Revision,
                IsReadOnly = !file.IsEditable,
            };
            _open.Add(of);
            _byKey[key] = of;
            ActiveKey = key;
        }
        Changed?.Invoke();
    }

    /// <summary>关闭标签。</summary>
    public void Close(FileResourceId id)
    {
        var key = Key(id);
        if (!_byKey.Remove(key, out var of)) return;
        _open.Remove(of);
        if (ActiveKey == key) ActiveKey = _open.Count > 0 ? Key(_open[^1].Id) : null;
        Changed?.Invoke();
    }

    /// <summary>激活标签。</summary>
    public void Activate(FileResourceId id)
    {
        if (_byKey.ContainsKey(Key(id))) { ActiveKey = Key(id); Changed?.Invoke(); }
    }

    /// <summary>标记未保存修改。</summary>
    public void MarkDirty(FileResourceId id, string content)
    {
        if (!_byKey.TryGetValue(Key(id), out var of)) return;
        of.Content = content; of.HasUnsavedChanges = true;
        Changed?.Invoke();
    }

    /// <summary>应用磁盘更新（仅当无未保存修改时覆盖内容）。</summary>
    public bool ApplyDiskUpdate(FileReadResult file)
    {
        if (!_byKey.TryGetValue(Key(file.Id), out var of)) return false;
        if (of.HasUnsavedChanges) return false;
        of.Content = file.Content ?? ""; of.OpenedRevision = file.Revision;
        of.IsReadOnly = !file.IsEditable; of.IsDeleted = false;
        Changed?.Invoke();
        return true;
    }

    /// <summary>应用重命名（含目录前缀重写）。</summary>
    public void ApplyRename(FileResourceId oldId, FileResourceId newId)
    {
        var prefix = oldId.RelativePath.TrimEnd('/') + "/";
        var toRename = _open.Where(o => o.Id.RelativePath == oldId.RelativePath
            || o.Id.RelativePath.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        foreach (var of in toRename)
        {
            _byKey.Remove(Key(of.Id));
            var newRel = of.Id.RelativePath == oldId.RelativePath
                ? newId.RelativePath
                : newId.RelativePath + of.Id.RelativePath[oldId.RelativePath.Length..];
            of.Id = new FileResourceId(newId.RootId, newRel);
            _byKey[Key(of.Id)] = of;
        }
        Changed?.Invoke();
    }

    /// <summary>标记文件已删除。</summary>
    public void MarkDeleted(FileResourceId id)
    {
        if (_byKey.TryGetValue(Key(id), out var of)) { of.IsDeleted = true; Changed?.Invoke(); }
    }

    /// <summary>设置 agent diff（供视图渲染）。</summary>
    public void SetPendingDiff(FileResourceId id, string? diff)
    {
        if (_byKey.TryGetValue(Key(id), out var of)) { of.PendingDiff = diff; Changed?.Invoke(); }
    }
}
```

- [ ] **Step 3: 写测试**

```csharp
using FluentAssertions;
using Seeing.Agent.Abstractions.Files;
using Seeing.Agent.WebUI.Files.Services;
using Xunit;

namespace Seeing.Agent.WebUI.Files.Tests;

public class FileTabsStateTests
{
    private static FileReadResult File(string rel, string content, bool editable = true) =>
        new(new FileResourceId("root", rel), null, content, FileContentKind.Text, "text/plain",
            "utf-8", editable, content.Length, 1, false, false, FileReadMode.Full);

    [Fact]
    public void Open_Then_Close_ShouldUpdateActive()
    {
        var s = new FileTabsState();
        s.Open(File("a.txt", "A"));
        s.Open(File("b.txt", "B"));
        s.ActiveKey.Should().Be("root:b.txt");
        s.Close(new FileResourceId("root", "b.txt"));
        s.ActiveKey.Should().Be("root:a.txt");
    }

    [Fact]
    public void ApplyDiskUpdate_DirtyTab_ShouldNotOverwrite()
    {
        var s = new FileTabsState();
        s.Open(File("a.txt", "A"));
        s.MarkDirty(new FileResourceId("root", "a.txt"), "A-edited");
        var applied = s.ApplyDiskUpdate(File("a.txt", "A-disk"));
        applied.Should().BeFalse();
        s.OpenFiles[0].Content.Should().Be("A-edited");
    }

    [Fact]
    public void ApplyRename_Directory_ShouldRewritePrefix()
    {
        var s = new FileTabsState();
        s.Open(File("dir/a.txt", "A"));
        s.ApplyRename(new FileResourceId("root", "dir"), new FileResourceId("root", "renamed"));
        s.OpenFiles[0].Id.RelativePath.Should().Be("renamed/a.txt");
    }
}
```

- [ ] **Step 4: 运行测试 + Commit**

```bash
git add samples/Seeing.Agent.WebUI.Files tests/apps/Seeing.Agent.WebUI.Files.Tests Seeing.Agent.slnx
git commit -m "feat(webui.files): RCL skeleton + FileTabsState"
```

---

#### Task 1.6: FilePanelState + FileViewerCoordinator

**Files:**
- Create: `samples/Seeing.Agent.WebUI.Files/Services/FilePanelState.cs`
- Create: `samples/Seeing.Agent.WebUI.Files/Services/FileViewerCoordinator.cs`
- Test: `tests/apps/Seeing.Agent.WebUI.Files.Tests/FileViewerCoordinatorTests.cs`

- [ ] **Step 1: 实现 FilePanelState（仅开合状态）**

```csharp
namespace Seeing.Agent.WebUI.Files.Services;

/// <summary>文件面板开合状态（Scoped，circuit 维度）。</summary>
public sealed class FilePanelState
{
    /// <summary>变更事件。</summary>
    public event Action? Changed;

    /// <summary>是否打开。</summary>
    public bool IsOpen { get; private set; }

    /// <summary>当前会话 id。</summary>
    public string? SessionId { get; private set; }

    /// <summary>打开。</summary>
    public void Open(string sessionId) { IsOpen = true; SessionId = sessionId; Changed?.Invoke(); }

    /// <summary>关闭。</summary>
    public void Close() { IsOpen = false; Changed?.Invoke(); }

    /// <summary>切换。</summary>
    public void Toggle(string sessionId) { if (IsOpen) Close(); else Open(sessionId); }
}
```

- [ ] **Step 2: 实现 FileViewerCoordinator**

```csharp
using Seeing.Agent.Abstractions.Files;

namespace Seeing.Agent.WebUI.Files.Services;

/// <summary>订阅磁盘变更流，驱动 FileTabsState 刷新/冲突。</summary>
public sealed class FileViewerCoordinator : IDisposable
{
    private readonly IFileWorkspace _workspace;
    private readonly FileTabsState _tabs;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>注入工作区与标签状态。</summary>
    public FileViewerCoordinator(IFileWorkspace workspace, FileTabsState tabs)
    {
        _workspace = workspace; _tabs = tabs;
    }

    /// <summary>启动订阅（回调在后台线程；调用方负责编组回 UI）。</summary>
    public void Start(Func<FileChangeEvent, Task> onEvent)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in _workspace.SubscribeChangesAsync(_cts.Token).ConfigureAwait(false))
                    await onEvent(evt).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        });
    }

    /// <summary>应用一个变更事件到标签状态（纯逻辑，可在测试中直接调用）。</summary>
    public async Task<ApplyOutcome> ApplyAsync(FileChangeEvent evt)
    {
        if (evt.Kind == FileChangeKind.Renamed && evt.OldPath is { } oldPath)
        {
            var newId = evt.Id;
            var oldId = _workspace.GetResourceId(oldPath);
            _tabs.ApplyRename(oldId, newId);
            return ApplyOutcome.Renamed;
        }
        if (evt.Kind == FileChangeKind.Deleted)
        {
            _tabs.MarkDeleted(evt.Id);
            return ApplyOutcome.Deleted;
        }
        var file = await _workspace.ReadAsync(_workspace.GetAbsolutePath(evt.Id), FileReadMode.Display).ConfigureAwait(false);
        var applied = _tabs.ApplyDiskUpdate(file);
        return applied ? ApplyOutcome.Refreshed : ApplyOutcome.ConflictSkipped;
    }

    /// <inheritdoc />
    public void Dispose() { _cts.Cancel(); _cts.Dispose(); }
}

/// <summary>变更应用结果。</summary>
public enum ApplyOutcome { Refreshed, ConflictSkipped, Deleted, Renamed }
```

- [ ] **Step 3: 写测试**

```csharp
using FluentAssertions;
using Moq;
using Seeing.Agent.Abstractions.Files;
using Seeing.Agent.WebUI.Files.Services;
using Xunit;

namespace Seeing.Agent.WebUI.Files.Tests;

public class FileViewerCoordinatorTests
{
    [Fact]
    public async Task ApplyAsync_DirtyTab_ShouldSkip()
    {
        var id = new FileResourceId("root", "a.txt");
        var tabs = new FileTabsState();
        tabs.Open(new FileReadResult(id, null, "A", FileContentKind.Text, "text/plain", "utf-8", true, 1, 1, false, false, FileReadMode.Full));
        tabs.MarkDirty(id, "edited");

        var ws = new Mock<IFileWorkspace>();
        ws.Setup(w => w.GetAbsolutePath(It.IsAny<FileResourceId>())).Returns("x");
        ws.Setup(w => w.ReadAsync(It.IsAny<string>(), It.IsAny<FileReadMode>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync(new FileReadResult(id, null, "disk", FileContentKind.Text, "text/plain", "utf-8", true, 4, 1, false, false, FileReadMode.Display));

        var coord = new FileViewerCoordinator(ws.Object, tabs);
        var outcome = await coord.ApplyAsync(new FileChangeEvent(id, FileChangeKind.Modified, null, null, DateTime.UtcNow));
        outcome.Should().Be(ApplyOutcome.ConflictSkipped);
    }
}
```

- [ ] **Step 4: 运行 + Commit**

```bash
git add samples/Seeing.Agent.WebUI.Files/Services tests/apps/Seeing.Agent.WebUI.Files.Tests
git commit -m "feat(webui.files): panel state + viewer coordinator"
```

---

#### Task 1.7: Razor 组件（FilePanelHost / FileTree / FileTabs / FileViewer / FileEditor / FilePanelToggle）

**Files:**
- Create: `samples/Seeing.Agent.WebUI.Files/Components/FilePanelHost.razor`
- Create: `samples/Seeing.Agent.WebUI.Files/Components/FileTree.razor`
- Create: `samples/Seeing.Agent.WebUI.Files/Components/FileTabs.razor`
- Create: `samples/Seeing.Agent.WebUI.Files/Components/FileViewer.razor`
- Create: `samples/Seeing.Agent.WebUI.Files/Components/FileEditor.razor`
- Create: `samples/Seeing.Agent.WebUI.Files/Components/FilePanelToggle.razor`
- Create: `samples/Seeing.Agent.WebUI.Files/wwwroot/files-panel.css`

> 本 Task 为薄渲染层，不自动化测试（仓库无 bUnit/Playwright harness），由 REVIEW GATE #2 人工验证。

- [ ] **Step 1: FilePanelHost.razor**

```razor
@using Seeing.Agent.Abstractions.Files
@using Seeing.Agent.WebUI.Files.Services
@inject IFileWorkspaceFactory WorkspaceFactory
@inject FilePanelState PanelState
@implements IDisposable

<div class="file-panel-host">
    @if (string.IsNullOrEmpty(SessionId))
    {
        <div class="file-panel-empty">无会话</div>
    }
    else if (_workspace is null)
    {
        <div class="file-panel-loading">加载中…</div>
    }
    else
    {
        <div class="file-panel-body">
            <FileTree Workspace="_workspace" OnOpen="OnOpenFileAsync" />
            <div class="file-panel-main">
                <FileTabs Tabs="_tabs" />
                <FileViewer Tabs="_tabs" />
            </div>
        </div>
    }
</div>

@code {
    [CascadingParameter(Name = "SessionId")] private string? SessionId { get; set; }

    private IFileWorkspace? _workspace;
    private readonly FileTabsState _tabs = new();
    private FileViewerCoordinator? _coordinator;

    protected override async Task OnInitializedAsync()
    {
        if (string.IsNullOrEmpty(SessionId)) return;
        _workspace = await WorkspaceFactory.ResolveAsync(SessionId);
        _coordinator = new FileViewerCoordinator(_workspace, _tabs);
        _coordinator.Start(async evt => await InvokeAsync(async () =>
        {
            await _coordinator!.ApplyAsync(evt);
            StateHasChanged();
        }));
        _tabs.Changed += OnTabsChanged;
        PanelState.Open(SessionId);
    }

    private void OnTabsChanged() => _ = InvokeAsync(StateHasChanged);

    private async Task OnOpenFileAsync(FileResourceId id)
    {
        if (_workspace is null) return;
        var file = await _workspace.ReadAsync(_workspace.GetAbsolutePath(id), FileReadMode.Display);
        _tabs.Open(file);
    }

    /// <summary>宿主桥接入口：agent 改动某文件。</summary>
    public async Task NotifyAgentChangeAsync(FileResourceId id, string? diff)
    {
        if (_workspace is null) return;
        var file = await _workspace.ReadAsync(_workspace.GetAbsolutePath(id), FileReadMode.Display);
        _tabs.Open(file);
        _tabs.SetPendingDiff(id, diff);
    }

    public void Dispose()
    {
        _tabs.Changed -= OnTabsChanged;
        _coordinator?.Dispose();
        if (!string.IsNullOrEmpty(SessionId)) WorkspaceFactory.Release(SessionId);
        PanelState.Close();
    }
}
```

- [ ] **Step 2: FileTree.razor**

```razor
@using Seeing.Agent.Abstractions.Files
@using AntDesign
@inject IFileWorkspaceFactory Factory
<div class="file-tree">
    <Tree TItem="FileEntry" DataSource="_roots" KeyExpression="@(e => e.Id.ToString())"
          ChildrenExpression="@(e => _children.TryGetValue(e.Id.ToString(), out var c) ? c : null)"
          IsLeafExpression="@(e => !e.IsDirectory)"
          TitleExpression="@(e => e.Name)"
          OnClick="@(e => OnOpen.InvokeAsync(e.Id))" />
</div>

@code {
    [Parameter] public IFileWorkspace Workspace { get; set; } = default!;
    [Parameter] public EventCallback<FileResourceId> OnOpen { get; set; }

    private List<FileEntry> _roots = [];
    private readonly Dictionary<string, List<FileEntry>> _children = new();

    protected override async Task OnInitializedAsync()
    {
        _roots = (await Workspace.ListAsync(Workspace.RootPath)).ToList();
    }
}
```

> AntDesign `Tree` 的懒加载 API 版本差异较大；若 `1.6.2` 不支持上述签名，改用 `Tree` + `LoadData` 或退化为简单递归 `<ul>`。**REVIEW GATE #2 需确认树能展开。**

- [ ] **Step 3: FileTabs.razor**

```razor
@using Seeing.Agent.WebUI.Files.Services
<div class="file-tabs">
    @foreach (var f in Tabs.OpenFiles)
    {
        <div class="file-tab @(Tabs.ActiveKey == f.Id.ToString() ? "active" : "")" @onclick="() => Tabs.Activate(f.Id)">
            <span>@f.Id.RelativePath</span>
            @if (f.HasUnsavedChanges) { <span class="dirty">●</span> }
            <button @onclick:stopPropagation="true" @onclick="() => Tabs.Close(f.Id)">×</button>
        </div>
    }
</div>

@code {
    [Parameter] public FileTabsState Tabs { get; set; } = default!;
}
```

- [ ] **Step 4: FileViewer.razor（分派 + diff 视图）**

```razor
@using Seeing.Agent.Abstractions.Files
@using Seeing.Agent.WebUI.Files.Services
@inject IFileWorkspaceFactory Factory
<div class="file-viewer">
    @{ var f = Tabs.OpenFiles.FirstOrDefault(x => x.Id.ToString() == Tabs.ActiveKey); }
    @if (f is null) { <div class="file-viewer-empty">选择文件</div> }
    else if (f.IsDeleted) { <div class="file-viewer-deleted">文件已删除</div> }
    else
    {
        @if (!string.IsNullOrEmpty(f.PendingDiff))
        {
            <pre class="file-diff">@f.PendingDiff</pre>
        }
        <pre class="file-content">@f.Content</pre>
    }
</div>

@code {
    [Parameter] public FileTabsState Tabs { get; set; } = default!;
}
```

> Markdown/图片/Monaco 分派为 P2/P3 增强；本 Task 先以 `<pre>` 展示（满足只读查看），Monaco 编辑在 Task 3.2。

- [ ] **Step 5: FileEditor.razor（Monaco 编辑 + 保存）**

```razor
@using Seeing.Agent.Abstractions.Files
@using Seeing.Agent.WebUI.Files.Services
@inject IFileWorkspaceFactory Factory
<div class="file-editor">
    @{ var f = Tabs.OpenFiles.FirstOrDefault(x => x.Id.ToString() == Tabs.ActiveKey); }
    @if (f is not null)
    {
        <button disabled="@(f.IsReadOnly)" @onclick="SaveAsync">保存</button>
        <textarea class="file-editor-input" disabled="@f.IsReadOnly"
                  value="@f.Content" @onchange="OnEdit"></textarea>
    }
</div>

@code {
    [Parameter] public FileTabsState Tabs { get; set; } = default!;
    [Parameter] public IFileWorkspace Workspace { get; set; } = default!;

    private void OnEdit(ChangeEventArgs e)
    {
        var f = Tabs.OpenFiles.FirstOrDefault(x => x.Id.ToString() == Tabs.ActiveKey);
        if (f is not null) Tabs.MarkDirty(f.Id, e.Value?.ToString() ?? "");
    }

    private async Task SaveAsync()
    {
        var f = Tabs.OpenFiles.FirstOrDefault(x => x.Id.ToString() == Tabs.ActiveKey);
        if (f is null) return;
        var result = await Workspace.WriteAsync(Workspace.GetAbsolutePath(f.Id), f.Content, f.OpenedRevision);
        if (result.Conflict) { /* TODO: 冲突弹窗，见 Task 3.2 */ }
        else { f.HasUnsavedChanges = false; f.OpenedRevision = result.Revision; }
    }
}
```

> Monaco 替换 `<textarea>` 属 Task 3.2（REVIEW GATE #3 后）。本 Task 先以 `<textarea>` 满足「可编辑保存」核心链路。

- [ ] **Step 6: FilePanelToggle.razor**

```razor
@using Seeing.Agent.WebUI.Files.Services
@inject FilePanelState PanelState
<button class="file-panel-toggle" @onclick="Toggle">
    <span>文件</span>
</button>

@code {
    [CascadingParameter(Name = "SessionId")] private string? SessionId { get; set; }
    private void Toggle() => PanelState.Toggle(SessionId ?? "");
}
```

- [ ] **Step 7: files-panel.css**

```css
.file-panel-host { display: flex; flex-direction: column; height: 100%; overflow: hidden; }
.file-panel-body { display: flex; flex: 1; overflow: hidden; }
.file-tree { width: 220px; flex-shrink: 0; overflow-y: auto; border-right: 1px solid var(--color-border, #eee); }
.file-panel-main { flex: 1; display: flex; flex-direction: column; overflow: hidden; }
.file-tabs { display: flex; gap: 4px; padding: 4px; border-bottom: 1px solid var(--color-border, #eee); overflow-x: auto; }
.file-tab { padding: 2px 8px; cursor: pointer; white-space: nowrap; }
.file-tab.active { background: rgba(0,0,0,0.05); border-radius: 4px; }
.file-tab .dirty { color: #faad14; }
.file-viewer, .file-editor { flex: 1; overflow: auto; padding: 8px; }
.file-content, .file-diff { white-space: pre-wrap; font-family: monospace; font-size: 12px; }
.file-diff { background: rgba(250,173,20,0.08); }
```

- [ ] **Step 8: 构建 + Commit**

Run: `dotnet build samples/Seeing.Agent.WebUI.Files/Seeing.Agent.WebUI.Files.csproj`
Expected: Build succeeded

```bash
git add samples/Seeing.Agent.WebUI.Files
git commit -m "feat(webui.files): razor components (tree/tabs/viewer/editor/toggle)"
```

---

### 🔍 **REVIEW GATE #2**（关键审查点）

**审查内容：**
1. 能力包测试全绿：`dotnet vstest tests/capabilities/Seeing.Agent.Files.Tests/bin/Debug/net10.0/Seeing.Agent.Files.Tests.dll`
2. 组件库测试全绿：`dotnet vstest tests/apps/Seeing.Agent.WebUI.Files.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Files.Tests.dll`
3. 能力包**无** `IExecutionEventPublisher`/`ToolCallEvent` 引用（grep 确认）。
4. 组件库**无**会话事件引用。
5. 守门测试 `Full_Should_Enable_All_Implemented_Module_Ids` 通过。

**通过后**启动 Wave 2。

---

## Wave 2 — WebUI 集成（依赖 Wave 1 全部完成）

### Task 2.1: UI 贡献 + 宿主桥接 + 会话页挂载

**Files:**
- Create: `samples/Seeing.Agent.WebUI/Services/FilePanelUiContribution.cs`
- Create: `samples/Seeing.Agent.WebUI/Services/AgentFileChangeBridge.cs`
- Create: `samples/Seeing.Agent.WebUI/Components/SessionFilePanel.razor`
- Modify: `samples/Seeing.Agent.WebUI/Program.cs`
- Modify: `samples/Seeing.Agent.WebUI/Components/SessionWindow.razor:59-65`
- Modify: `samples/Seeing.Agent.WebUI/Components/SessionToolbar.razor:141-189`
- Modify: `samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj`
- Modify: `samples/Seeing.Agent.WebUI/wwwroot/css/session-page.css`

- [ ] **Step 1: 加项目引用**

`Seeing.Agent.WebUI.csproj` 的 `<ItemGroup>` 内加：

```xml
    <ProjectReference Include="..\..\src\capabilities\Seeing.Agent.Files\Seeing.Agent.Files.csproj" />
    <ProjectReference Include="..\Seeing.Agent.WebUI.Files\Seeing.Agent.WebUI.Files.csproj" />
```

- [ ] **Step 2: 实现 AgentFileChangeBridge**

```csharp
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Files;
using Seeing.Agent.WebUI.Services;

namespace Seeing.Agent.WebUI.Services;

/// <summary>会话事件 → 文件变更的宿主桥接（circuit 维度 IStreamConsumer）。</summary>
public sealed class AgentFileChangeBridge : IStreamConsumer
{
    private readonly IFileWorkspaceFactory _factory;
    private string? _bound;

    /// <summary>agent 改动文件事件（id + 可选 diff）。</summary>
    public event Func<FileResourceId, string?, Task>? AgentChanged;

    /// <summary>注入工厂。</summary>
    public AgentFileChangeBridge(IFileWorkspaceFactory factory) => _factory = factory;

    /// <inheritdoc />
    public string SessionId => _bound ?? string.Empty;

    /// <summary>绑定父会话。</summary>
    public void Rebind(string sessionId) => _bound = sessionId;

    /// <inheritdoc />
    public void OnEvent(IMessageEvent evt)
    {
        if (evt is not ToolCallEvent tc) return;
        if (tc.Status != ToolCallStatus.Success) return;
        if (tc.ToolName is not ("write" or "edit" or "delete")) return;

        var path = ExtractPath(tc);
        if (string.IsNullOrEmpty(path)) return;

        var diff = tc.Metadata is not null && tc.Metadata.TryGetValue("diff", out var d) ? d?.ToString() : null;
        _ = DispatchAsync(tc.SessionId, path!, diff);
    }

    private static string? ExtractPath(ToolCallEvent tc)
    {
        if (tc.Metadata is not null && tc.Metadata.TryGetValue("path", out var p) && p is not null)
            return p.ToString();
        if (tc.Arguments is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.Object
            && je.TryGetProperty("path", out var argPath))
            return argPath.GetString();
        return null;
    }

    private async Task DispatchAsync(string sessionId, string path, string? diff)
    {
        if (AgentChanged is null) return;
        try
        {
            var ws = await _factory.ResolveAsync(sessionId).ConfigureAwait(false);
            var id = ws.GetResourceId(path);
            await AgentChanged(id, diff).ConfigureAwait(false);
        }
        catch { /* 桥接失败不致命 */ }
    }

    /// <inheritdoc />
    public void OnStreamEnd() { }
}
```

> **注意**：`ResolveAsync` 会增引用计数，桥接侧不配对 `Release`（由 `FilePanelHost` 持有/释放）。若计数语义需要严格配对，改为「桥接只算 id」——但 `GetResourceId` 需要 workspace 的 RootId。**REVIEW GATE #3 确认引用计数无泄漏**（可让桥接在 `Rebind`/`OnStreamEnd` 时对旧会话 `Release`）。

- [ ] **Step 3: 实现 SessionFilePanel.razor**

```razor
@using Seeing.Agent.Abstractions.Files
@using Seeing.Agent.WebUI.Files.Components
@using Seeing.Agent.WebUI.Services
@inject SessionEventStreamRouter Router
@inject CircuitContext CircuitContext
@implements IDisposable

<FilePanelHost @ref="_host" />

@code {
    [CascadingParameter(Name = "SessionId")] private string? SessionId { get; set; }

    private FilePanelHost? _host;
    private AgentFileChangeBridge? _bridge;

    protected override void OnInitialized()
    {
        if (string.IsNullOrEmpty(SessionId)) return;
        _bridge = Router.GetOrCreateCircuitConsumer<AgentFileChangeBridge>(CircuitContext.Id ?? "");
        _bridge.Rebind(SessionId);
        _bridge.AgentChanged += OnAgentChanged;
        Router.AttachConsumer(SessionId, _bridge);
    }

    private Task OnAgentChanged(FileResourceId id, string? diff) => InvokeAsync(() =>
        _host is null ? Task.CompletedTask : _host.NotifyAgentChangeAsync(id, diff));

    public void Dispose()
    {
        if (_bridge is not null)
        {
            _bridge.AgentChanged -= OnAgentChanged;
            if (!string.IsNullOrEmpty(SessionId)) Router.DetachConsumer(SessionId, _bridge);
        }
    }
}
```

- [ ] **Step 4: 实现 FilePanelUiContribution**

```csharp
using Seeing.Agent.Abstractions.Ui;
using Seeing.Agent.WebUI.Components;
using Seeing.Agent.WebUI.Files.Components;

namespace Seeing.Agent.WebUI.Services;

/// <summary>文件面板 UI 贡献（宿主层包装组件 + 工具栏切换）。</summary>
public sealed class FilePanelUiContribution : IUiContribution
{
    /// <inheritdoc />
    public string ModuleId => "files.workspace";

    /// <inheritdoc />
    public IReadOnlyList<object> Contribute() =>
    [
        new SlotContribution("session_file_panel", typeof(SessionFilePanel), Requires: ["files.workspace"]),
        new SlotContribution("session_toolbar_actions", typeof(FilePanelToggle), Requires: ["files.workspace"]),
    ];
}
```

- [ ] **Step 5: Program.cs 注册**

在 `builder.Services.AddSeeingModule<FileSystemModule>(registry);`（:62）后加：

```csharp
builder.Services.AddSeeingModule<Seeing.Agent.Files.FileWorkspaceModule>(registry);
```

在服务注册区（`AddScoped<SessionWindowRegistry>()` 附近，:127）加：

```csharp
builder.Services.AddScoped<Seeing.Agent.WebUI.Services.AgentFileChangeBridge>();
builder.Services.AddScoped<Seeing.Agent.WebUI.Files.Services.FilePanelState>();
```

在初始化后（`uiRegistry.Register(new CoreShellUiContribution())` 附近，:220）加：

```csharp
uiRegistry.Register(new Seeing.Agent.WebUI.Services.FilePanelUiContribution());
```

- [ ] **Step 6: SessionWindow.razor 级联 SessionId + 插槽**

`SessionWindow.razor:59` 处，把 `SessionScenario` 级联包一层 `SessionId`：

```razor
        <CascadingValue Name="SessionScenario" Value="@SessionState.CurrentSession?.Scenario">
        <CascadingValue Name="SessionId" Value="@SessionId">
        <SessionToolbar Context="Context"
                        Mode="ViewMode"
                        LeftActions="ToolbarLeftActions"
                        RightActions="ToolbarRightActions" />
        <div Class="session-content-wrapper">
            <div Class="session-main-column">
                ...
            </div>

            @* Todo 侧边面板 *@
            @if (SessionState.CurrentTodoList != null && SessionState.CurrentTodoList.HasTasks)
            {
                <div Class="session-todo-panel">
                    <TodoPanel TodoList="@SessionState.CurrentTodoList" />
                </div>
            }

            @* 文件面板插槽（模块启用时由 SlotHost 渲染） *@
            <SlotHost SlotName="session_file_panel" />
        </div>
        </CascadingValue>
        </CascadingValue>
```

> 注意：`SlotHost` 与 `.session-content-wrapper` 的 flex 布局；文件面板样式见 Step 8。

- [ ] **Step 7: SessionToolbar.razor 工具栏插槽**

`SessionToolbar.razor:141` 的 `<div class="session-toolbar-right">` 内、`@if (RightActions != null)` 之前加：

```razor
            <SlotHost SlotName="session_toolbar_actions" />
```

并在文件顶部 `@using` 区加 `@using Seeing.Agent.WebUI.Shared`（`SlotHost` 命名空间）。

- [ ] **Step 8: CSS**

`session-page.css` 末尾加：

```css
.session-file-panel {
    width: 480px;
    flex-shrink: 0;
    border-left: 1px solid var(--color-border);
    background: var(--color-bg-container);
    overflow: hidden;
}
@media (max-width: 1200px) {
    .session-file-panel {
        position: absolute;
        right: 0;
        top: 0;
        bottom: 0;
        z-index: 10;
        width: 100%;
        max-width: 480px;
        box-shadow: -2px 0 8px rgba(0,0,0,0.15);
    }
}
```

> `SlotHost` 渲染出的 `SessionFilePanel` 需包在 `.session-file-panel` 容器里。若 `SlotHost` 外层是 `.slot-host`，在 CSS 里让 `.slot-host[data-slot="session_file_panel"]` 承担 `.session-file-panel` 样式，或调整 `SlotHost` 支持容器 class。**REVIEW GATE #3 确认布局。**

- [ ] **Step 9: 构建 + 手工验证**

Run: `dotnet build Seeing.Agent.slnx`
Expected: Build succeeded

手工：`dotnet run --project samples/Seeing.Agent.WebUI` → 打开会话 → 点「文件」→ 应显示文件树；agent 写文件后对应标签刷新。

- [ ] **Step 10: Commit**

```bash
git add samples/Seeing.Agent.WebUI Seeing.Agent.slnx
git commit -m "feat(webui): file panel slot contribution + agent change bridge + session mount"
```

---

### 🔍 **REVIEW GATE #3**（最终关键审查）

**审查内容：**
1. 全量构建：`dotnet build Seeing.Agent.slnx` 通过。
2. 全部测试：能力包 + 组件库 + Core 守门测试全绿。
3. 手工验证三条：文件树懒加载、多标签切换、agent `write` 后标签自动刷新。
4. 模块门控：`.seeing/seeing.json` 禁用 `files.workspace` → 无面板、无按钮。
5. 引用计数无泄漏（Task 2.1 Step 2 注释）。
6. 布局：双右侧面板（Todo + 文件）窄屏不崩。

**通过后**交付 P2 完成；P3（Monaco + 冲突弹窗）作为后续增量。

---

## 后续（P3 增量，不在本次交付）

- Task 3.1: Markdown/图片渲染（Markdig + `<img>`）。
- Task 3.2: Monaco 编辑器替换 `<textarea>` + 冲突弹窗（`WriteResult.Conflict` → 重载/保留）。
- Task 3.3: watcher 稳定性双采样增强。
- Task 3.4: P4 现有文件工具改 facade 适配器（收敛唯一真相源）。

---

## Self-Review（对照 spec）

| spec 章节 | 覆盖任务 |
|-----------|----------|
| §4.1 Core 端口 | Task 0.1（契约）+ 0.2（实现/门闸） |
| §4.2/4.3 文件契约/DTO | Task 0.1 |
| §5.1 工厂 | Task 1.1 |
| §5.2 FileWorkspace | Task 1.2 |
| §5.3 PathWriteLocks | Task 1.2 Step 1 |
| §5.4/5.5 watcher | Task 1.3 |
| §5.6 HostedService | Task 1.4 |
| §5.7 Module | Task 1.4 |
| §6 组件库 | Task 1.5–1.7 |
| §7.1 注册 | Task 2.1 Step 5 |
| §7.2 UI 贡献+级联 | Task 2.1 Step 4/6/7 |
| §7.3 宿主桥接 | Task 2.1 Step 2/3 |
| §7.4 样式 | Task 2.1 Step 8 |
| §8 并发/正确性 | Task 1.2（编码/截断/revision） |
| §9 安全 | Task 0.2（门闸） |
| §10 测试 | 各 Task 测试步骤 |
| §11 分期 | Wave 0/1/2 + 后续 |

**已知偏差（计划允许的简化，需在 REVIEW GATE 确认）：**
- 截断读取仍整文件读入（`MaxReadBytes` 未真正流式采样）——P1 可接受，增强留后续。
- `PathWriteLocks.MaxLocks` 淘汰未实现（仅引用计数回收）。
- 组件渲染层用 `<textarea>`/`<pre>` 起步，Monaco/Markdig 留 P3。
- `FileWatcherPool` 稳定性双采样未实现（仅防抖）。
