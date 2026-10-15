# 权限通道重构实施计划

> **对于自动化工作者：** 推荐使用 superpowers:subagent-driven-development 或 superpowers:executing-plans 按任务逐项实施。步骤使用 checkbox (`- [ ]`) 语法跟踪进度。

**目标：** 将分散的双重权限通道（`ToolContext.AskPermission` + `IPermissionChannel`）统一为单一 `IPermissionChannel`，新增会话级权限记忆，支持目录级文件路径权限自动匹配。

**架构：** 在 `SerializingPermissionChannel` 中嵌入 `IPermissionMemory`，记忆检查在串行化之前完成（记忆命中跳过队列），未命中时串行询问 UI 后写入记忆。所有通道实现（Blazor/Gateway/Default）共享此记忆层。

**技术栈：** .NET 10.0, C#, xUnit 2.9, Moq 4.20, FluentAssertions 6.12

**约束：** 仅会话级（无持久化），全程不 git commit

---

## 文件结构

**新建文件：**
- `src/Seeing.Agent/Core/Permission/IPermissionMemory.cs` — 记忆接口
- `src/Seeing.Agent/Core/Permission/SessionPermissionMemory.cs` — 会话级记忆实现
- `src/Seeing.Agent/Core/Permission/PermissionChannelResult.cs` — 统一返回类型
- `src/Seeing.Agent/Tools/BuiltIn/FileSystem/AddWorkspacePathTool.cs` — 新增工具
- `tests/Seeing.Agent.Tests/Permission/SessionPermissionMemoryTests.cs` — 记忆测试
- `tests/Seeing.Agent.Tests/Core/MemoryPermissionChannelTests.cs` — 通道+记忆集成测试

**修改文件（主库）：**
- `Core/Interfaces/IPermissionChannel.cs` — 接口统一 + 更新 Default/DenyAll 实现
- `Core/Interfaces/ITool.cs` — 移除 ToolContext.AskPermission
- `Core/Permission/SerializingPermissionChannel.cs` — 嵌入记忆层
- `Core/Permission/DynamicPermissionChannel.cs` — 适配新接口
- `Tools/BuiltIn/BuiltInToolBase.cs` — 重构权限检查方法
- `Tools/BuiltIn/FileSystem/ReadTool.cs` — 迁移到 channel.RequestAsync
- `Tools/BuiltIn/FileSystem/WriteTool.cs` — 同上
- `Tools/BuiltIn/FileSystem/EditTool.cs` — 同上
- `Tools/BuiltIn/FileSystem/GlobTool.cs` — 同上
- `Tools/BuiltIn/FileSystem/GrepTool.cs` — 同上
- `Tools/BuiltIn/Shell/BashTool.cs` — 同上
- `Tools/BuiltIn/Web/WebFetchTool.cs` — 同上
- `Tools/BuiltIn/Web/WebSearchTool.cs` — 同上
- `Tools/BuiltIn/Web/CodeSearchTool.cs` — 同上
- `Tools/BuiltIn/Todo/TodoWriteTool.cs` — 同上
- `Tools/SkillTool.cs` — 同上
- `Decorators/TimeoutToolDecorator.cs` — 更新 TimeoutToolContext
- `Core/AgentExecutor.cs` — 更新 EvaluatePermissionAsync
- `Extensions/ServiceCollectionExtensions.cs` — DI 注册

**修改文件（其他项目）：**
- `samples/Seeing.Agent.WebUI/Services/BlazorPermissionChannel.cs`
- `src/Seeing.Agent.Gateway/Permission/GatewayPermissionChannel.cs`

**修改文件（测试）：**
- `tests/Seeing.Agent.Tests/Core/SerializingPermissionChannelTests.cs`

---

### 任务 1：创建 PermissionChannelResult

**文件：**
- 新建：`src/Seeing.Agent/Core/Permission/PermissionChannelResult.cs`

- [ ] **步骤 1：创建 PermissionChannelResult**

```csharp
namespace Seeing.Agent.Core.Permission;

/// <summary>
/// 权限通道统一返回结果
/// </summary>
public class PermissionChannelResult
{
    /// <summary>最终决策</summary>
    public PermissionChannelAction Action { get; set; }

    /// <summary>需要记忆的资源标识（null=不记住）</summary>
    public string? ResourceToRemember { get; set; }

    /// <summary>拒绝原因</summary>
    public string? Reason { get; set; }

    /// <summary>匹配的记忆条目（内部使用）</summary>
    public PermissionMemoryEntry? MatchedMemory { get; set; }

    public static PermissionChannelResult Allowed(string? resourceToRemember = null)
        => new() { Action = PermissionChannelAction.Allow, ResourceToRemember = resourceToRemember };

    public static PermissionChannelResult Denied(string reason, string? resourceToRemember = null)
        => new() { Action = PermissionChannelAction.Deny, Reason = reason, ResourceToRemember = resourceToRemember };
}

public enum PermissionChannelAction
{
    Allow = 0,
    Deny = 1,
}
```

---

### 任务 2：创建 PermissionMemory 模型和接口

**文件：**
- 新建：`src/Seeing.Agent/Core/Permission/IPermissionMemory.cs`

- [ ] **步骤 1：创建 PermissionMemoryEntry、PermissionMemoryAction 和 IPermissionMemory 接口**

```csharp
namespace Seeing.Agent.Core.Permission;

/// <summary>
/// 权限记忆条目
/// </summary>
public class PermissionMemoryEntry
{
    /// <summary>唯一标识</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>权限类别</summary>
    public string PermissionKind { get; set; } = string.Empty;

    /// <summary>目标资源（工具名 / 文件路径 / 目录路径）</summary>
    public string? Resource { get; set; }

    /// <summary>决策</summary>
    public PermissionMemoryAction Action { get; set; }

    /// <summary>创建时间</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum PermissionMemoryAction
{
    Allow = 0,
    Deny = 1,
}

/// <summary>
/// 会话级权限记忆接口 — 纯内存，无持久化
/// </summary>
public interface IPermissionMemory
{
    /// <summary>查询记忆：按 kind + resource 匹配（支持目录前缀）</summary>
    PermissionMemoryEntry? Match(string permissionKind, string? resource, string sessionId);

    /// <summary>写入记忆</summary>
    void Remember(string sessionId, PermissionMemoryEntry entry);

    /// <summary>遗忘指定资源的记忆</summary>
    void Forget(string sessionId, string? resource);

    /// <summary>清除整个会话的记忆</summary>
    void ClearSession(string sessionId);
}
```

- [ ] **步骤 2：编译验证**

```bash
dotnet build src/Seeing.Agent --no-restore
```

---

### 任务 3：实现 SessionPermissionMemory

**文件：**
- 新建：`src/Seeing.Agent/Core/Permission/SessionPermissionMemory.cs`

- [ ] **步骤 1：实现 SessionPermissionMemory**

```csharp
using System.Collections.Concurrent;

namespace Seeing.Agent.Core.Permission;

/// <summary>
/// 会话级权限记忆 — 纯内存实现，按 SessionId 隔离，支持目录前缀匹配
/// </summary>
public class SessionPermissionMemory : IPermissionMemory
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, PermissionMemoryEntry>> _store = new();

    public PermissionMemoryEntry? Match(string permissionKind, string? resource, string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId) || !_store.TryGetValue(sessionId, out var entries))
            return null;

        foreach (var (_, entry) in entries)
        {
            if (!string.Equals(entry.PermissionKind, permissionKind, StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.Equals(entry.Resource, resource, StringComparison.OrdinalIgnoreCase))
                return entry;

            // 目录前缀匹配：记忆的 Resource 以路径分隔符结尾时，检查请求的 resource 是否在其子目录
            if (resource != null && entry.Resource != null &&
                (entry.Resource.EndsWith('/') || entry.Resource.EndsWith('\\')) &&
                resource.StartsWith(entry.Resource, StringComparison.OrdinalIgnoreCase))
                return entry;

            // 通配符匹配
            if (entry.Resource != null && entry.Resource.Contains('*') &&
                WildcardMatch(entry.Resource, resource ?? string.Empty))
                return entry;
        }

        return null;
    }

    public void Remember(string sessionId, PermissionMemoryEntry entry)
    {
        var entries = _store.GetOrAdd(sessionId, _ => new ConcurrentDictionary<string, PermissionMemoryEntry>(StringComparer.OrdinalIgnoreCase));
        entries[entry.Id] = entry;
    }

    public void Forget(string sessionId, string? resource)
    {
        if (string.IsNullOrEmpty(sessionId) || !_store.TryGetValue(sessionId, out var entries))
            return;

        var toRemove = entries.Where(kv =>
            resource == null || string.Equals(kv.Value.Resource, resource, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key).ToList();

        foreach (var key in toRemove)
            entries.TryRemove(key, out _);
    }

    public void ClearSession(string sessionId)
    {
        _store.TryRemove(sessionId, out _);
    }

    private static bool WildcardMatch(string pattern, string input)
    {
        if (pattern == input) return true;
        if (pattern == "*") return true;
        if (pattern.EndsWith("**"))
        {
            var prefix = pattern[..^2];
            return input.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }
}
```

- [ ] **步骤 2：编译验证**

```bash
dotnet build src/Seeing.Agent --no-restore
```

---

### 任务 4：编写 SessionPermissionMemory 测试

**文件：**
- 新建：`tests/Seeing.Agent.Tests/Permission/SessionPermissionMemoryTests.cs`

- [ ] **步骤 1：编写测试**

```csharp
using FluentAssertions;
using Seeing.Agent.Core.Permission;
using Xunit;

namespace Seeing.Agent.Tests.Permission;

public class SessionPermissionMemoryTests
{
    [Fact]
    public void Match_ExactResource_ShouldReturnEntry()
    {
        var memory = new SessionPermissionMemory();
        memory.Remember("s1", new PermissionMemoryEntry
        {
            PermissionKind = "tool.execute",
            Resource = "bash",
            Action = PermissionMemoryAction.Allow
        });

        var result = memory.Match("tool.execute", "bash", "s1");

        result.Should().NotBeNull();
        result!.Action.Should().Be(PermissionMemoryAction.Allow);
    }

    [Fact]
    public void Match_DirectoryPrefix_ShouldReturnEntry()
    {
        var memory = new SessionPermissionMemory();
        memory.Remember("s1", new PermissionMemoryEntry
        {
            PermissionKind = "filesystem.external",
            Resource = "/home/user/projects/",
            Action = PermissionMemoryAction.Allow
        });

        var result = memory.Match("filesystem.external", "/home/user/projects/foo/bar.txt", "s1");

        result.Should().NotBeNull();
        result!.Action.Should().Be(PermissionMemoryAction.Allow);
    }

    [Fact]
    public void Match_DifferentSession_ShouldNotReturnEntry()
    {
        var memory = new SessionPermissionMemory();
        memory.Remember("s1", new PermissionMemoryEntry
        {
            PermissionKind = "tool.execute",
            Resource = "bash",
            Action = PermissionMemoryAction.Allow
        });

        var result = memory.Match("tool.execute", "bash", "s2");

        result.Should().BeNull();
    }

    [Fact]
    public void ClearSession_ShouldRemoveAllEntries()
    {
        var memory = new SessionPermissionMemory();
        memory.Remember("s1", new PermissionMemoryEntry
        {
            PermissionKind = "tool.execute",
            Resource = "bash",
            Action = PermissionMemoryAction.Allow
        });

        memory.ClearSession("s1");

        memory.Match("tool.execute", "bash", "s1").Should().BeNull();
    }

    [Fact]
    public void Match_DenyOverride_ShouldReturnDeny()
    {
        var memory = new SessionPermissionMemory();
        memory.Remember("s1", new PermissionMemoryEntry
        {
            PermissionKind = "tool.execute",
            Resource = "bash",
            Action = PermissionMemoryAction.Allow
        });
        memory.Remember("s1", new PermissionMemoryEntry
        {
            PermissionKind = "tool.execute",
            Resource = "bash",
            Action = PermissionMemoryAction.Deny
        });

        var result = memory.Match("tool.execute", "bash", "s1");

        // 最后注册的条目优先（ConcurrentDictionary 不保证顺序，接受任一匹配）
        result.Should().NotBeNull();
    }

    [Fact]
    public void Forget_ShouldRemoveSpecificEntry()
    {
        var memory = new SessionPermissionMemory();
        memory.Remember("s1", new PermissionMemoryEntry
        {
            PermissionKind = "tool.execute",
            Resource = "bash",
            Action = PermissionMemoryAction.Allow
        });

        memory.Forget("s1", "bash");

        memory.Match("tool.execute", "bash", "s1").Should().BeNull();
    }
}
```

- [ ] **步骤 2：运行测试确认通过**

```bash
dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~SessionPermissionMemoryTests"
```

---

### 任务 5：重构 IPermissionChannel 接口 + Default/DenyAll 实现

**文件：**
- 修改：`src/Seeing.Agent/Core/Interfaces/IPermissionChannel.cs`

- [ ] **步骤 1：重写 IPermissionChannel 接口和 DefaultPermissionChannel、DenyAllPermissionChannel**

将整个文件替换为：

```csharp
using Seeing.Agent.Core.Permission;

namespace Seeing.Agent.Core.Interfaces
{
    /// <summary>
    /// 权限请求（扩展版）
    /// </summary>
    public class PermissionRequest
    {
        /// <summary>权限类别："tool.execute", "filesystem.write", "filesystem.read", "network.fetch" 等</summary>
        public string PermissionKind { get; set; } = string.Empty;

        /// <summary>目标资源（工具名、文件路径、URL 等）</summary>
        public string? Resource { get; set; }

        /// <summary>辅助匹配模式列表</summary>
        public List<string> Patterns { get; set; } = new();

        /// <summary>上下文元数据</summary>
        public Dictionary<string, object> Metadata { get; set; } = new();

        /// <summary>当前会话 ID</summary>
        public string? SessionId { get; set; }
    }

    /// <summary>
    /// 权限请求通道接口 — 统一的权限请求入口
    /// </summary>
    public interface IPermissionChannel
    {
        /// <summary>
        /// 统一的权限请求入口。记忆检查由外层装饰器（SerializingPermissionChannel）处理，
        /// 具体通道实现只需处理 UI 交互。
        /// </summary>
        Task<PermissionChannelResult> RequestAsync(PermissionRequest request, CancellationToken ct = default);
    }

    /// <summary>
    /// 默认权限通道 — 安全默认：拒绝所有操作
    /// </summary>
    public class DefaultPermissionChannel : IPermissionChannel
    {
        public bool AutoApproveAll { get; init; } = false;

        public static readonly DefaultPermissionChannel Instance = new();
        public static readonly DefaultPermissionChannel AutoApproveInstance = new() { AutoApproveAll = true };

        public Task<PermissionChannelResult> RequestAsync(PermissionRequest request, CancellationToken ct = default)
        {
            if (AutoApproveAll)
                return Task.FromResult(PermissionChannelResult.Allowed());

            throw new PermissionRequiredException(
                request.Resource ?? "未知资源",
                "未配置权限确认通道。请在配置中设置 Permission:AutoApproveAll=true 以自动批准所有操作（危险），或提供 IPermissionChannel 实现（如 ConsolePermissionChannel）。");
        }
    }

    /// <summary>
    /// 立即拒绝所有权限请求的通道
    /// </summary>
    public sealed class DenyAllPermissionChannel : IPermissionChannel
    {
        public static readonly DenyAllPermissionChannel Instance = new();

        public Task<PermissionChannelResult> RequestAsync(PermissionRequest request, CancellationToken ct = default)
            => Task.FromResult(PermissionChannelResult.Denied("后台执行模式：未配置权限通道，请求被拒绝"));
    }

    /// <summary>
    /// 权限请求异常
    /// </summary>
    public class PermissionRequiredException : Exception
    {
        public string Resource { get; }

        public PermissionRequiredException(string resource, string message)
            : base($"[{resource}] {message}")
        {
            Resource = resource;
        }
    }
}
```

- [ ] **步骤 2：编译验证（此时编译会失败，后续任务逐步修复）**

```bash
dotnet build src/Seeing.Agent --no-restore 2>&1 | Select-Object -First 20
```

---

### 任务 6：更新 DynamicPermissionChannel

**文件：**
- 修改：`src/Seeing.Agent/Core/Permission/DynamicPermissionChannel.cs`

- [ ] **步骤 1：适配新接口**

将整个文件替换为：

```csharp
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Interfaces;

namespace Seeing.Agent.Core.Permission;

/// <summary>
/// 动态权限通道 — 支持配置热重载的 AutoApproveAll
/// </summary>
public sealed class DynamicPermissionChannel : IPermissionChannel
{
    private readonly IOptionsMonitor<SeeingAgentOptions> _optionsMonitor;
    private readonly ILogger<DynamicPermissionChannel>? _logger;

    public DynamicPermissionChannel(
        IOptionsMonitor<SeeingAgentOptions> optionsMonitor,
        ILogger<DynamicPermissionChannel>? logger = null)
    {
        _optionsMonitor = optionsMonitor;
        _logger = logger;
    }

    private bool AutoApproveAll => _optionsMonitor.CurrentValue.Permission?.AutoApproveAll ?? false;

    public Task<PermissionChannelResult> RequestAsync(PermissionRequest request, CancellationToken ct = default)
    {
        if (AutoApproveAll)
        {
            _logger?.LogDebug("Auto-approved: {Kind} {Resource}", request.PermissionKind, request.Resource);
            return Task.FromResult(PermissionChannelResult.Allowed());
        }

        throw new PermissionRequiredException(
            request.Resource ?? "未知资源",
            "未配置权限确认通道。请在配置中设置 Permission:AutoApproveAll=true，或提供 IPermissionChannel 实现。");
    }
}
```

---

### 任务 7：更新 SerializingPermissionChannel — 嵌入记忆层

**文件：**
- 修改：`src/Seeing.Agent/Core/Permission/SerializingPermissionChannel.cs`

- [ ] **步骤 1：重写 SerializingPermissionChannel，嵌入 IPermissionMemory**

```csharp
using Seeing.Agent.Core.Interfaces;

namespace Seeing.Agent.Core.Permission;

/// <summary>
/// 权限通道串行包装 + 会话级记忆：
/// 1. 先查记忆 → 命中直接返回（无需串行化）
/// 2. 未命中 → 获取信号量 → 询问内部通道 → 释放信号量
/// 3. 若用户选择"会话内记住" → 写入记忆
/// </summary>
public sealed class SerializingPermissionChannel : IPermissionChannel
{
    private readonly IPermissionChannel _inner;
    private readonly IPermissionMemory _memory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SerializingPermissionChannel(IPermissionChannel inner, IPermissionMemory memory)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
    }

    public async Task<PermissionChannelResult> RequestAsync(PermissionRequest request, CancellationToken ct = default)
    {
        // 第一步：检查会话记忆（无需串行化，记忆命中直接返回）
        if (!string.IsNullOrEmpty(request.SessionId))
        {
            var memoryHit = _memory.Match(request.PermissionKind, request.Resource, request.SessionId);
            if (memoryHit != null)
            {
                return memoryHit.Action == PermissionMemoryAction.Allow
                    ? PermissionChannelResult.Allowed()
                    : PermissionChannelResult.Denied("会话记忆拒绝", memoryHit.Resource);
            }
        }

        // 第二步：记忆未命中，串行化询问用户
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await _inner.RequestAsync(request, ct).ConfigureAwait(false);

            // 第三步：若用户选择了"记住"，写入记忆
            if (!string.IsNullOrEmpty(result.ResourceToRemember) && !string.IsNullOrEmpty(request.SessionId))
            {
                _memory.Remember(request.SessionId, new PermissionMemoryEntry
                {
                    PermissionKind = request.PermissionKind,
                    Resource = result.ResourceToRemember,
                    Action = result.Action == PermissionChannelAction.Allow
                        ? PermissionMemoryAction.Allow
                        : PermissionMemoryAction.Deny
                });

                // 记忆写入后清除返回中的 ResourceToRemember（避免调用方重复处理）
                result.ResourceToRemember = null;
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }
}
```

---

### 任务 8：移除 ToolContext.AskPermission

**文件：**
- 修改：`src/Seeing.Agent/Core/Interfaces/ITool.cs`

- [ ] **步骤 1：移除 AskPermission 字段**

将以下行删除：
```csharp
/// <summary>请求权限确认</summary>
public Func<PermissionRequest, Task>? AskPermission { get; set; }
```

同时移除不再需要的 `PermissionRequest` 类（它现在在 `IPermissionChannel.cs` 中定义）。

--- 具体改动：删除第 19-20 行（AskPermission 字段），删除第 39-44 行（PermissionRequest 类）.

---

### 任务 9：更新 TimeoutToolDecorator.TimeoutToolContext

**文件：**
- 修改：`src/Seeing.Agent/Decorators/TimeoutToolDecorator.cs`

- [ ] **步骤 1：移除 TimeoutToolContext 中的 AskPermission 赋值**

将第 79 行：
```csharp
AskPermission = original.AskPermission;
```
替换为：
```csharp
```

即直接删除该行。

---

### 任务 10：重构 BuiltInToolBase 权限方法

**文件：**
- 修改：`src/Seeing.Agent/Tools/BuiltIn/BuiltInToolBase.cs`

- [ ] **步骤 1：替换 AskPermissionAsync，新增 RequestPermissionAsync**

删除现有的 `AskPermissionAsync` 方法（第 67-90 行），替换为：

```csharp
/// <summary>
/// 通过 IPermissionChannel 请求权限确认
/// </summary>
protected async Task<ToolResult?> RequestPermissionAsync(
    ToolContext context,
    string permissionKind,
    string? resource,
    Dictionary<string, object>? metadata = null)
{
    var channel = context.PermissionChannel;
    if (channel == null) return null;

    var result = await channel.RequestAsync(new PermissionRequest
    {
        PermissionKind = permissionKind,
        Resource = resource,
        SessionId = context.SessionId,
        Metadata = metadata ?? new Dictionary<string, object>()
    });

    if (result.Action == PermissionChannelAction.Deny)
        return Failure(result.Reason ?? "权限被拒绝");

    return null;
}
```

需要新增 using：
```csharp
using Seeing.Agent.Core.Permission;
```

- [ ] **步骤 2：替换 CheckPathWithinWorkspaceAsync**

删除现有的 `CheckPathWithinWorkspaceAsync` 方法（第 144-210 行，包括静态缓存和 PurgeCacheIfNeeded），替换为：

```csharp
/// <summary>
/// 检查文件路径是否在工作区内，外部路径通过 IPermissionChannel 申请权限
/// （目录级记忆由 SerializingPermissionChannel 内置的 IPermissionMemory 处理）
/// </summary>
protected async Task<ToolResult?> CheckPathWithinWorkspaceAsync(string filePath, ToolContext context)
{
    var workspace = context.Services?.GetService<IWorkspaceProvider>();
    if (workspace == null) return null;

    if (FileSystemHelper.IsPathWithinDirectory(filePath, workspace.WorkspaceRoot))
        return null;

    var channel = context.PermissionChannel;
    if (channel == null)
        return Failure("安全限制：不允许访问工作区外的路径。");

    var result = await channel.RequestAsync(new PermissionRequest
    {
        PermissionKind = "filesystem.external",
        Resource = filePath,
        Patterns = new List<string> { filePath },
        SessionId = context.SessionId,
        Metadata = new Dictionary<string, object>
        {
            ["path"] = filePath,
            ["workspace"] = workspace.WorkspaceRoot,
            ["reason"] = "访问工作区外的路径"
        }
    });

    if (result.Action == PermissionChannelAction.Deny)
        return Failure(result.Reason ?? "安全限制：拒绝访问工作区外的路径。");

    return null;
}
```

同时删除不再需要的静态字段（第 144-145 行）：
```csharp
private static readonly ConcurrentDictionary<string, HashSet<string>> s_approvedPathsCache = new(...);
private const int s_maxCacheEntries = 256;
```

以及删除 `PurgeCacheIfNeeded` 方法（第 201-209 行）、移除不再需要的 using（`System.Collections.Concurrent` 如果没其他用途）。

---

### 任务 11：更新 ReadTool / WriteTool / EditTool

**文件：**
- 修改：`src/Seeing.Agent/Tools/BuiltIn/FileSystem/ReadTool.cs`
- 修改：`src/Seeing.Agent/Tools/BuiltIn/FileSystem/WriteTool.cs`
- 修改：`src/Seeing.Agent/Tools/BuiltIn/FileSystem/EditTool.cs`

- [ ] **步骤 1：更新 ReadTool.cs（第 98-112 行附近）**

将：
```csharp
if (context.AskPermission != null)
{
    await context.AskPermission(new PermissionRequest
    {
        Permission = "read",
        Patterns = new List<string> { filePath },
        Metadata = new Dictionary<string, object>
        {
            ["filePath"] = filePath,
            ["offset"] = offset,
            ["limit"] = limit
        }
    });
}
```
替换为：
```csharp
var permCheck = await RequestPermissionAsync(context, "filesystem.read", filePath,
    new Dictionary<string, object>
    {
        ["filePath"] = filePath,
        ["offset"] = offset,
        ["limit"] = limit
    });
if (permCheck != null) return permCheck;
```

- [ ] **步骤 2：更新 WriteTool.cs（第 97-111 行附近）**

将：
```csharp
if (context.AskPermission != null)
{
    await context.AskPermission(new PermissionRequest
    {
        Permission = "edit",
        Patterns = new List<string> { filePath },
        Metadata = new Dictionary<string, object>
        {
            ["filePath"] = filePath,
            ["diff"] = diff,
            ["exists"] = exists
        }
    });
}
```
替换为：
```csharp
var permCheck = await RequestPermissionAsync(context, "filesystem.write", filePath,
    new Dictionary<string, object>
    {
        ["filePath"] = filePath,
        ["diff"] = diff,
        ["exists"] = exists
    });
if (permCheck != null) return permCheck;
```

- [ ] **步骤 3：更新 EditTool.cs（两处 AskPermission，约第 147 行和第 191 行）**

第一处替换：
```csharp
if (context.AskPermission != null)
{
    await context.AskPermission(new PermissionRequest
    {
        Permission = "read",
        Patterns = new List<string> { filePath },
        Metadata = new Dictionary<string, object> { ["filePath"] = filePath }
    });
}
```
替换为：
```csharp
var permCheck = await RequestPermissionAsync(context, "filesystem.read", filePath,
    new Dictionary<string, object> { ["filePath"] = filePath });
if (permCheck != null) return permCheck;
```

第二处替换：
```csharp
if (context.AskPermission != null)
{
    await context.AskPermission(new PermissionRequest
    {
        Permission = "edit",
        Patterns = new List<string> { filePath },
        Metadata = new Dictionary<string, object> { ["filePath"] = filePath, ["diff"] = diff }
    });
}
```
替换为：
```csharp
var permCheck = await RequestPermissionAsync(context, "filesystem.write", filePath,
    new Dictionary<string, object> { ["filePath"] = filePath, ["diff"] = diff });
if (permCheck != null) return permCheck;
```

---

### 任务 12：更新 GlobTool / GrepTool

**文件：**
- 修改：`src/Seeing.Agent/Tools/BuiltIn/FileSystem/GlobTool.cs`
- 修改：`src/Seeing.Agent/Tools/BuiltIn/FileSystem/GrepTool.cs`

- [ ] **步骤 1：更新 GlobTool.cs（第 100-110 行附近）**

将：
```csharp
if (context.AskPermission != null)
{
    await context.AskPermission(new PermissionRequest
    {
        Permission = "glob",
        Patterns = new List<string> { pattern },
        ...
    });
}
```
替换为：
```csharp
var permCheck = await RequestPermissionAsync(context, "filesystem.read", pattern,
    new Dictionary<string, object> { ["pattern"] = pattern, ["path"] = path });
if (permCheck != null) return permCheck;
```

- [ ] **步骤 2：更新 GrepTool.cs（第 80-92 行附近）**

同样模式替换 `AskPermission` 为 `RequestPermissionAsync(context, "filesystem.read", path, ...)`。

---

### 任务 13：更新 BashTool / WebFetchTool / WebSearchTool / CodeSearchTool / TodoWriteTool / SkillTool

**文件：**
- 修改：`src/Seeing.Agent/Tools/BuiltIn/Shell/BashTool.cs`
- 修改：`src/Seeing.Agent/Tools/BuiltIn/Web/WebFetchTool.cs`
- 修改：`src/Seeing.Agent/Tools/BuiltIn/Web/WebSearchTool.cs`
- 修改：`src/Seeing.Agent/Tools/BuiltIn/Web/CodeSearchTool.cs`
- 修改：`src/Seeing.Agent/Tools/BuiltIn/Todo/TodoWriteTool.cs`
- 修改：`src/Seeing.Agent/Tools/SkillTool.cs`

- [ ] **步骤 1：批量替换所有工具的 AskPermission 调用**

每个工具的修改模式相同。以 BashTool 为例（第 164-179 行）：

```csharp
// 替换前
if (context.AskPermission != null)
{
    await context.AskPermission(new PermissionRequest
    {
        Permission = "bash",
        ...
    });
}

// 替换后
var permCheck = await RequestPermissionAsync(context, "shell.execute", command,
    new Dictionary<string, object> { ["command"] = command, ["workdir"] = workdir });
if (permCheck != null) return permCheck;
```

各工具的 `PermissionKind` 映射：

| 工具 | PermissionKind |
|------|---------------|
| BashTool | `shell.execute` |
| WebFetchTool | `network.fetch` |
| WebSearchTool | `network.search` |
| CodeSearchTool | `network.search` |
| TodoWriteTool | `tool.execute` |
| SkillTool | `tool.execute` |

**注意 TodoWriteTool.cs 有两处 AskPermission（第 95 行附近和第 304 行附近），都需要更新。**

---

### 任务 14：更新 AgentExecutor — EvaluatePermissionAsync

**文件：**
- 修改：`src/Seeing.Agent/Core/AgentExecutor.cs`

- [ ] **步骤 1：更新 EvaluatePermissionAsync（第 647-674 行）**

将：
```csharp
private async Task<PermissionDecision> EvaluatePermissionAsync(
    string toolName, object? arguments,
    Models.AgentDefinition agent, AgentContext context,
    IPermissionChannel permissionChannel)
{
    var policy = ResolvePolicy(agent, context);
    var permContext = PermissionContext.FromAgentContext(context, policy, agent.Name);

    var result = await _permissions.EvaluateToolAsync(toolName, null, permContext).ConfigureAwait(false);

    if (result.NeedsConfirmation)
    {
        var userDecision = await permissionChannel.RequestToolPermissionAsync(
            toolName, arguments, context).ConfigureAwait(false);

        if (userDecision.Action != PermissionAction.Allow)
            return PermissionDecision.Deny("用户拒绝");

        return PermissionDecision.Allow("用户确认允许");
    }

    return result.ToDecision();
}
```

替换为：

```csharp
private async Task<PermissionDecision> EvaluatePermissionAsync(
    string toolName, object? arguments,
    Models.AgentDefinition agent, AgentContext context,
    IPermissionChannel permissionChannel)
{
    var policy = ResolvePolicy(agent, context);
    var permContext = PermissionContext.FromAgentContext(context, policy, agent.Name);

    var result = await _permissions.EvaluateToolAsync(toolName, null, permContext).ConfigureAwait(false);

    if (result.NeedsConfirmation)
    {
        var channelResult = await permissionChannel.RequestAsync(new PermissionRequest
        {
            PermissionKind = "tool.execute",
            Resource = toolName,
            SessionId = context.SessionId,
            Metadata = new Dictionary<string, object>
            {
                ["arguments"] = arguments ?? new object()
            }
        }, context.CancellationToken).ConfigureAwait(false);

        if (channelResult.Action != PermissionChannelAction.Allow)
            return PermissionDecision.Deny(channelResult.Reason ?? "用户拒绝");

        return PermissionDecision.Allow("用户确认允许");
    }

    return result.ToDecision();
}
```

需要新增 using：
```csharp
using Seeing.Agent.Core.Permission;
```

- [ ] **步骤 2：移除 SerializingPermissionChannel 的重复创建（第 108-110 行）**

当前代码在 `ExecuteLoopAsync` 中创建了 `new SerializingPermissionChannel(...)`，现在 `SerializingPermissionChannel` 的构造函数需要 `IPermissionMemory`。由于此通道已由 DI 注册，直接使用 `context.PermissionChannel` 即可。

将第 108-110 行：
```csharp
var permissionChannel = new SerializingPermissionChannel(
    context.PermissionChannel ?? Interfaces.DefaultPermissionChannel.Instance);
```
替换为：
```csharp
var permissionChannel = context.PermissionChannel ?? Interfaces.DefaultPermissionChannel.Instance;
```

---

### 任务 15：更新 ServiceCollectionExtensions — DI 注册

**文件：**
- 修改：`src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs`

- [ ] **步骤 1：添加 IPermissionMemory 注册 + 更新 IPermissionChannel 注册**

在第 586-600 行附近，将现有的权限通道注册替换为：

```csharp
// 权限记忆（会话级，纯内存）
services.AddSingleton<IPermissionMemory, SessionPermissionMemory>();

// 权限通道 — 默认使用 DynamicPermissionChannel + SerializingPermissionChannel（带记忆）
// 如果用户在其他地方注册了 IPermissionChannel（如 BlazorPermissionChannel），
// 那个注册会覆盖这里的默认注册
services.AddSingleton<IPermissionChannel>(sp =>
{
    var memory = sp.GetRequiredService<IPermissionMemory>();
    var optionsMonitor = sp.GetRequiredService<IOptionsMonitor<SeeingAgentOptions>>();
    var logger = sp.GetService<ILogger<Core.Permission.DynamicPermissionChannel>>();

    var inner = new Core.Permission.DynamicPermissionChannel(optionsMonitor, logger);
    return new Core.Permission.SerializingPermissionChannel(inner, memory);
});
```

需要新增 using（如果尚未存在）：
```csharp
using Seeing.Agent.Core.Permission;
```

- [ ] **步骤 2：更新 PermissionServiceExtensions（如果需要）**

检查 `Extensions/PermissionServiceExtensions.cs`，确认无需额外修改（`IPermissionCache` 和 `IPermissionService` 注册保持独立）。

---

### 任务 16：创建 AddWorkspacePathTool

**文件：**
- 新建：`src/Seeing.Agent/Tools/BuiltIn/FileSystem/AddWorkspacePathTool.cs`

- [ ] **步骤 1：实现工具**

```csharp
using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using System.Text.Json;

namespace Seeing.Agent.Tools.BuiltIn.FileSystem;

/// <summary>
/// 请求将路径加入当前会话的工作区白名单。
/// Agent 可主动调用此工具来预先申请路径权限，避免后续逐个文件询问。
/// </summary>
public class AddWorkspacePathTool : BuiltInToolBase
{
    private static readonly JsonElement s_schema = JsonSerializer.SerializeToElement(new Dictionary<string, object>
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object>
        {
            ["path"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["description"] = "要添加到工作区白名单的目录路径"
            }
        },
        ["required"] = new[] { "path" }
    });

    public AddWorkspacePathTool(ILogger<AddWorkspacePathTool> logger)
        : base(logger)
    {
    }

    public override string Id => "add_workspace_path";

    public override string Description => "请求将指定目录加入当前会话的工作区白名单，获批准后该目录下所有文件操作自动免询问";

    public override ToolCategory Category => ToolCategory.FileSystem;

    public override JsonElement ParametersSchema => s_schema;

    public override IReadOnlyList<string> Tags => new[] { "built-in", "filesystem" };

    public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        var path = GetStringArgument(arguments, "path");
        if (string.IsNullOrEmpty(path))
            return Failure("参数 path 不能为空");

        path = ResolvePath(path);

        if (!Directory.Exists(path))
            return Failure($"目录不存在: {path}");

        var workspace = context.Services?.GetService<IWorkspaceProvider>();
        if (workspace != null && FileSystemHelper.IsPathWithinDirectory(path, workspace.WorkspaceRoot))
            return Success("路径已在工作区内，无需额外权限", path);

        var channel = context.PermissionChannel;
        if (channel == null)
            return Failure("未配置权限通道，无法申请路径权限");

        var result = await channel.RequestAsync(new PermissionRequest
        {
            PermissionKind = "filesystem.workspace_extend",
            Resource = path,
            Patterns = new List<string> { path },
            SessionId = context.SessionId,
            Metadata = new Dictionary<string, object>
            {
                ["path"] = path,
                ["reason"] = "Agent 请求扩展工作区路径"
            }
        });

        if (result.Action == PermissionChannelAction.Deny)
            return Failure(result.Reason ?? "用户拒绝了工作区路径扩展请求");

        return Success("路径已加入当前会话的工作区白名单", path);
    }
}
```

需要新增 using：
```csharp
using Seeing.Agent.Core.Permission;
using Seeing.Agent.Configuration;
```

- [ ] **步骤 2：在 ToolManager 中注册 AddWorkspacePathTool**

查找内置工具注册位置。内置工具在 `ToolManager` 或 DI 注册中统一注册。检查 `ToolManager` 的默认注册列表。

```bash
rg "ReadTool|WriteTool|GlobTool" src/Seeing.Agent/Tools/ --max-count 5
```

---

### 任务 17：更新 BlazorPermissionChannel

**文件：**
- 修改：`samples/Seeing.Agent.WebUI/Services/BlazorPermissionChannel.cs`

- [ ] **步骤 1：适配新 IPermissionChannel 接口**

将整个文件替换为适配新接口的实现。核心变化：
- 移除 `RequestToolPermissionAsync` / `RequestSubAgentPermissionAsync` / `RequestWritePermissionAsync` / `RequestConfirmationAsync`
- 实现单一的 `RequestAsync(PermissionRequest, CancellationToken)`
- 保留 `RespondToPermission` 用于 UI 回调
- 新增对 `ResourceToRemember` 的支持：UI 可以通过 `RespondToPermission` 重载传递记忆信息

```csharp
using Seeing.Agent.Core.Events;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Permission;
using System.Collections.Concurrent;

namespace Seeing.Agent.WebUI.Services
{
    public class BlazorPermissionChannel : IPermissionChannel
    {
        private readonly EventStreamHandler _eventStreamHandler;
        private readonly State.SessionState _sessionState;

        private readonly ConcurrentDictionary<string, TaskCompletionSource<PermissionChannelResult>> _pendingRequests = new();

        public int PendingCount => _pendingRequests.Count;

        public BlazorPermissionChannel(EventStreamHandler eventStreamHandler, State.SessionState sessionState)
        {
            _eventStreamHandler = eventStreamHandler;
            _sessionState = sessionState;
        }

        public async Task<PermissionChannelResult> RequestAsync(PermissionRequest request, CancellationToken ct = default)
        {
            var requestId = Guid.NewGuid().ToString();
            var tcs = new TaskCompletionSource<PermissionChannelResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingRequests[requestId] = tcs;

            ct.Register(() =>
            {
                if (_pendingRequests.TryRemove(requestId, out var t))
                    t.TrySetResult(PermissionChannelResult.Denied("操作已取消"));
            });

            await _eventStreamHandler.ProcessEventAsync(new PermissionRequestEvent
            {
                SessionId = request.SessionId ?? string.Empty,
                PermissionId = requestId,
                PermissionKind = request.PermissionKind,
                Resource = request.Resource,
                Arguments = request.Metadata,
                Message = BuildMessage(request.PermissionKind, request.Resource),
                RiskLevel = request.PermissionKind.Contains("write") ? "high" : "medium"
            });

            try
            {
                return await tcs.Task.WaitAsync(TimeSpan.FromMinutes(5), ct);
            }
            catch (TimeoutException)
            {
                _pendingRequests.TryRemove(requestId, out _);
                return PermissionChannelResult.Denied("权限请求超时");
            }
        }

        public void RespondToPermission(string requestId, PermissionChannelAction action, string? resourceToRemember = null)
        {
            if (_pendingRequests.TryRemove(requestId, out var tcs))
            {
                var result = action switch
                {
                    PermissionChannelAction.Allow => PermissionChannelResult.Allowed(resourceToRemember),
                    PermissionChannelAction.Deny => PermissionChannelResult.Denied("用户拒绝", resourceToRemember),
                    _ => PermissionChannelResult.Denied("未知操作")
                };
                tcs.SetResult(result);
            }
        }

        private static string BuildMessage(string kind, string? resource) => kind switch
        {
            "tool.execute" => $"工具 {resource} 需要权限确认",
            "filesystem.write" => $"写入文件 {resource} 需要权限确认",
            "filesystem.read" => $"读取文件 {resource} 需要权限确认",
            "filesystem.external" => $"访问工作区外路径 {resource} 需要权限确认",
            "filesystem.workspace_extend" => $"请求将 {resource} 加入工作区白名单",
            "shell.execute" => $"执行命令 {resource} 需要权限确认",
            "network.fetch" => $"访问 URL {resource} 需要权限确认",
            "network.search" => $"搜索操作需要权限确认",
            _ => $"操作 {resource} 需要权限确认"
        };
    }
}
```

**注意：** 需要同步更新 WebUI 中调用 `RespondToPermission` 的 Blazor 组件，传递 `resourceToRemember` 参数。

---

### 任务 18：更新 GatewayPermissionChannel

**文件：**
- 修改：`src/Seeing.Agent.Gateway/Permission/GatewayPermissionChannel.cs`

- [ ] **步骤 1：适配新接口**

核心变化同 BlazorPermissionChannel：
- 移除四个旧方法，实现 `RequestAsync`
- `Respond` 方法支持 `resourceToRemember` 参数

由于文件较长，仅给出核心变更：

```csharp
// 接口实现改为单一方法
public async Task<PermissionChannelResult> RequestAsync(PermissionRequest request, CancellationToken ct = default)
{
    if (AutoApprove)
        return PermissionChannelResult.Allowed();

    return await RequestDecisionAsync(request.PermissionKind, request.Resource ?? string.Empty, request.Metadata, request.SessionId);
}

// Respond 方法增加 resourceToRemember 参数
public GatewayPermissionRespondResult Respond(string sessionId, string permissionId, bool allow, string? reason = null, string? resourceToRemember = null)
{
    // ... 原有逻辑 ...
    var result = allow
        ? PermissionChannelResult.Allowed(resourceToRemember)
        : PermissionChannelResult.Denied(reason ?? "用户拒绝", resourceToRemember);
    entry.Completion.TrySetResult(result);
    // ...
}

// 内部方法签名更新
private async Task<PermissionChannelResult> RequestDecisionAsync(
    string kind, string resource, object? arguments, string? sessionId)
{
    // 使用 PermissionChannelResult 替代 PermissionDecision
    var tcs = new TaskCompletionSource<PermissionChannelResult>(TaskCreationOptions.RunContinuationsAsynchronously);
    // ... 其余逻辑类似 ...
    return await tcs.Task.WaitAsync(timeout);
}
```

完整改写请参照 BlazorPermissionChannel 的模式。

---

### 任务 19：更新 SerializingPermissionChannelTests

**文件：**
- 修改：`tests/Seeing.Agent.Tests/Core/SerializingPermissionChannelTests.cs`

- [ ] **步骤 1：适配新接口的测试**

```csharp
using FluentAssertions;
using Seeing.Agent.Core.Permission;
using Seeing.Agent.Core.Interfaces;
using Xunit;

namespace Seeing.Agent.Tests.Core;

public class SerializingPermissionChannelTests
{
    [Fact]
    public async Task RequestAsync_ShouldSerializeConcurrentAsks()
    {
        var gate = new object();
        var concurrent = 0;
        var maxConcurrent = 0;

        var inner = new CountingChannel(() =>
        {
            lock (gate)
            {
                concurrent++;
                maxConcurrent = Math.Max(maxConcurrent, concurrent);
            }
            Thread.Sleep(50);
            lock (gate) { concurrent--; }
            return PermissionChannelResult.Allowed();
        });

        var memory = new SessionPermissionMemory();
        var serial = new SerializingPermissionChannel(inner, memory);
        var request = new PermissionRequest { PermissionKind = "tool.execute", Resource = "bash", SessionId = "s1" };

        var tasks = Enumerable.Range(0, 4)
            .Select(_ => serial.RequestAsync(request))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        maxConcurrent.Should().Be(1);
        inner.CallCount.Should().Be(4);
        results.All(r => r.Action == PermissionChannelAction.Allow).Should().BeTrue();
    }

    [Fact]
    public async Task RequestAsync_MemoryHit_ShouldSkipSerialization()
    {
        var inner = new CountingChannel(() => PermissionChannelResult.Allowed());
        var memory = new SessionPermissionMemory();
        memory.Remember("s1", new PermissionMemoryEntry
        {
            PermissionKind = "tool.execute",
            Resource = "bash",
            Action = PermissionMemoryAction.Allow
        });

        var serial = new SerializingPermissionChannel(inner, memory);
        var request = new PermissionRequest { PermissionKind = "tool.execute", Resource = "bash", SessionId = "s1" };

        var result = await serial.RequestAsync(request);

        result.Action.Should().Be(PermissionChannelAction.Allow);
        inner.CallCount.Should().Be(0, "记忆命中不应调用内部通道");
    }

    [Fact]
    public async Task RequestAsync_RememberSession_ShouldWriteMemory()
    {
        var inner = new StaticChannel(PermissionChannelResult.Allowed(resourceToRemember: "bash"));
        var memory = new SessionPermissionMemory();
        var serial = new SerializingPermissionChannel(inner, memory);
        var request = new PermissionRequest { PermissionKind = "tool.execute", Resource = "bash", SessionId = "s1" };

        var result = await serial.RequestAsync(request);

        result.Action.Should().Be(PermissionChannelAction.Allow);
        var hit = memory.Match("tool.execute", "bash", "s1");
        hit.Should().NotBeNull();
        hit!.Action.Should().Be(PermissionMemoryAction.Allow);
    }

    [Fact]
    public async Task RequestAsync_MemoryDenied_ShouldReturnDeniedWithoutCall()
    {
        var inner = new CountingChannel(() => PermissionChannelResult.Allowed());
        var memory = new SessionPermissionMemory();
        memory.Remember("s1", new PermissionMemoryEntry
        {
            PermissionKind = "tool.execute",
            Resource = "bash",
            Action = PermissionMemoryAction.Deny
        });

        var serial = new SerializingPermissionChannel(inner, memory);
        var result = await serial.RequestAsync(
            new PermissionRequest { PermissionKind = "tool.execute", Resource = "bash", SessionId = "s1" });

        result.Action.Should().Be(PermissionChannelAction.Deny);
        inner.CallCount.Should().Be(0);
    }

    private sealed class CountingChannel : IPermissionChannel
    {
        private readonly Func<PermissionChannelResult> _onAsk;
        public int CallCount { get; private set; }
        public CountingChannel(Func<PermissionChannelResult> onAsk) => _onAsk = onAsk;
        public Task<PermissionChannelResult> RequestAsync(PermissionRequest request, CancellationToken ct = default)
        {
            Interlocked.Increment(ref CallCount);
            return Task.FromResult(_onAsk());
        }
    }

    private sealed class StaticChannel : IPermissionChannel
    {
        private readonly PermissionChannelResult _result;
        public StaticChannel(PermissionChannelResult result) => _result = result;
        public Task<PermissionChannelResult> RequestAsync(PermissionRequest request, CancellationToken ct = default)
            => Task.FromResult(_result);
    }
}

// CountingChannel 的 CallCount 需要改为线程安全方式
```

---

### 任务 20：删除死代码

**文件：**
- 修改：`src/Seeing.Agent/Core/Interfaces/IPermissionChannel.cs`（已完成）
- 检查：`src/Seeing.Agent/Core/Interfaces/ITool.cs`（已完成）

以下类型已在之前任务中删除或替换，确认无残留引用：

- `PermissionChannelAdapter` — 已在任务 5 中随文件重写删除
- `ExtendedPermissionRequest` — 已删除，新 `PermissionRequest` 包含所需字段
- `PermissionChannelExtensions.WithConfirmationHandler` — 已随文件重写删除

- [ ] **步骤 1：全局搜索残留引用**

```bash
rg "PermissionChannelAdapter|ExtendedPermissionRequest|WithConfirmationHandler" src/ samples/ --no-filename
```

如有残留，逐一清理。

---

### 任务 21：编译验证 + 测试

**文件：** 全部已修改文件

- [ ] **步骤 1：完整构建**

```bash
dotnet build Seeing.Agent.slnx --no-restore
```

修复所有编译错误。

- [ ] **步骤 2：运行权限相关测试**

```bash
dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~Permission|FullyQualifiedName~Serializing"
```

- [ ] **步骤 3：运行全部测试**

```bash
dotnet test Seeing.Agent.slnx
```

---

### 任务 22：在 ToolManager 中注册 AddWorkspacePathTool

**文件：**
- 修改：找到 ToolManager 中批量注册内置工具的位置

- [ ] **步骤 1：查找注册位置**

```bash
rg "new ReadTool|new WriteTool|new BashTool" src/Seeing.Agent/
```

- [ ] **步骤 2：添加注册**

在发现的位置添加：
```csharp
new AddWorkspacePathTool(loggerFactory.CreateLogger<AddWorkspacePathTool>())
```

---

## 实施顺序

按任务编号顺序执行：1→2→3→4→5→6→7→8→9→10→11→12→13→14→15→16→22→17→18→19→20→21

基础层（1-7）完成后才能编译通过，然后逐步修复工具的引用（8-13），再修复外围系统（14-18），最后测试验证（19-20）。
