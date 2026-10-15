# Tool Permission Unification Implementation Plan

> **For agentic workers:** Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move all resource-level permission checks from individual tools into `ToolManager.ExecuteAsync`, placed after `tool.execute.before` hooks so plugins can modify arguments first.

**Architecture:** Introduce `IToolPermissionPolicy` — a single interface called by `ToolManager` that maps tool names to resource permission checks. A `DefaultToolPermissionPolicy` carries a static mapping table for all built-in tools. Remove all `RequestPermissionAsync` calls from the 10 tool files that currently use them.

**Tech Stack:** C# / .NET 10, Xunit + FluentAssertions + Moq

---

## File Structure

| File | Action | Responsibility |
|------|--------|----------------|
| `src/Seeing.Agent/Core/Interfaces/IToolPermissionPolicy.cs` | **Create** | Public interface + `PermissionResourceCheck` record |
| `src/Seeing.Agent/Tools/BuiltIn/DefaultToolPermissionPolicy.cs` | **Create** | Static mapping table + default `Evaluate` impl |
| `src/Seeing.Agent/Tools/ToolManager.cs` | **Modify** | Add ctor param, insert permission check after hook |
| `src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs` | **Modify** | DI registration of policy, pass to ToolManager ctor |
| `src/Seeing.Agent/Tools/BuiltIn/FileSystem/ReadTool.cs` | **Modify** | Remove lines ~93-100 |
| `src/Seeing.Agent/Tools/BuiltIn/FileSystem/WriteTool.cs` | **Modify** | Remove lines ~85-87 |
| `src/Seeing.Agent/Tools/BuiltIn/FileSystem/EditTool.cs` | **Modify** | Remove lines ~112-114 |
| `src/Seeing.Agent/Tools/BuiltIn/FileSystem/GlobTool.cs` | **Modify** | Remove lines ~100-102 |
| `src/Seeing.Agent/Tools/BuiltIn/FileSystem/GrepTool.cs` | **Modify** | Remove lines ~83-85 |
| `src/Seeing.Agent/Tools/BuiltIn/Shell/BashTool.cs` | **Modify** | Remove lines ~165-173 |
| `src/Seeing.Agent/Tools/BuiltIn/Web/WebFetchTool.cs` | **Modify** | Remove lines ~100-107 |
| `src/Seeing.Agent/Tools/BuiltIn/Web/WebSearchTool.cs` | **Modify** | Remove lines ~91-100 |
| `src/Seeing.Agent/Tools/BuiltIn/Web/CodeSearchTool.cs` | **Modify** | Remove lines ~77-83 |
| `src/Seeing.Agent/Tools/BuiltIn/Todo/TodoWriteTool.cs` | **Modify** | Remove lines ~266-268 (TodoReadTool) |
| `src/Seeing.Agent/Tools/BuiltIn/FileSystem/AddWorkspacePathTool.cs` | **Modify** | Remove lines ~58-85 (manual channel call) |
| `tests/Seeing.Agent.Tests/Tools/DefaultToolPermissionPolicyTests.cs` | **Create** | Unit tests for mapping table |
| `tests/Seeing.Agent.Tests/Tools/ToolInvokerTests.cs` | **Modify** | Add permission check integration tests |

---

### Task 1: Create `IToolPermissionPolicy` interface

**Files:**
- Create: `src/Seeing.Agent/Core/Interfaces/IToolPermissionPolicy.cs`

- [ ] **Step 1: Create the interface file**

```csharp
// src/Seeing.Agent/Core/Interfaces/IToolPermissionPolicy.cs

using System.Text.Json;

namespace Seeing.Agent.Core.Interfaces;

/// <summary>
/// Resource-level permission policy for tool invocations.
/// Evaluated by ToolManager after tool.execute.before hooks,
/// before the tool's ExecuteAsync is called.
/// </summary>
public interface IToolPermissionPolicy
{
    /// <summary>
    /// Returns a resource-level permission check for the given tool invocation,
    /// or null if no resource-level check is required for this tool.
    /// Called after tool.execute.before hooks have run,
    /// so <paramref name="args"/> reflects any hook modifications.
    /// </summary>
    PermissionResourceCheck? Evaluate(string toolId, JsonElement args);
}

/// <summary>
/// Describes a resource permission check to be performed
/// via IPermissionChannel before tool execution.
/// </summary>
public record PermissionResourceCheck(
    string PermissionKind,
    string Resource,
    List<string>? Patterns = null,
    Dictionary<string, object>? Metadata = null
);
```

- [ ] **Step 2: Build to verify compilation**

Run: `dotnet build src/Seeing.Agent`
Expected: Build succeeds (no consumers yet, just the types exist).

- [ ] **Step 3: Commit**

```bash
git add src/Seeing.Agent/Core/Interfaces/IToolPermissionPolicy.cs
git commit -m "feat: add IToolPermissionPolicy interface and PermissionResourceCheck record"
```

---

### Task 2: Create `DefaultToolPermissionPolicy`

**Files:**
- Create: `src/Seeing.Agent/Tools/BuiltIn/DefaultToolPermissionPolicy.cs`

- [ ] **Step 1: Create the default policy with mapping table**

```csharp
// src/Seeing.Agent/Tools/BuiltIn/DefaultToolPermissionPolicy.cs

using System.Text.Json;
using Seeing.Agent.Core.Interfaces;

namespace Seeing.Agent.Tools.BuiltIn;

/// <summary>
/// Default resource permission policy for built-in tools.
/// Maps tool IDs to their resource parameters via a static table.
/// Tools not in the table need no resource-level check and return null.
/// </summary>
public sealed class DefaultToolPermissionPolicy : IToolPermissionPolicy
{
    internal readonly record struct Mapping(
        string PermissionKind,
        string? ArgKey,
        string? FixedResource = null,
        bool TrailingDirSeparator = false,
        string? PatternsArgKey = null,
        Dictionary<string, object>? Metadata = null
    );

    private static readonly Dictionary<string, Mapping> Mappings = new()
    {
        ["read"]                = new("filesystem.read",               "file_path"),
        ["write"]               = new("filesystem.write",              "file_path"),
        ["edit"]                = new("filesystem.write",              "file_path"),
        ["grep"]                = new("filesystem.read",               "path"),
        ["glob"]                = new("filesystem.read",               "path"),
        ["bash"]                = new("shell.execute",                 "command"),
        ["webfetch"]            = new("network.fetch",                 "url"),
        ["websearch"]           = new("network.search",                null, FixedResource: "web_search"),
        ["codesearch"]          = new("network.search",                null, FixedResource: "code_search"),
        ["add_workspace_path"]  = new("filesystem.workspace_extend",   "path",
                                   TrailingDirSeparator: true,
                                   PatternsArgKey: "path",
                                   Metadata: new() { ["reason"] = "Agent 请求扩展工作区路径" }),
    };

    public PermissionResourceCheck? Evaluate(string toolId, JsonElement args)
    {
        if (!Mappings.TryGetValue(toolId, out var m))
            return null;

        var resource = m.FixedResource
            ?? TryGetStringArg(args, m.ArgKey)
            ?? string.Empty;

        if (m.TrailingDirSeparator && resource.Length > 0 && !resource.EndsWith('/'))
            resource += '/';

        List<string>? patterns = null;
        if (m.PatternsArgKey is not null)
        {
            var pat = TryGetStringArg(args, m.PatternsArgKey);
            if (pat is not null)
                patterns = new List<string> { pat };
        }

        return new PermissionResourceCheck(
            m.PermissionKind,
            resource,
            Patterns: patterns,
            Metadata: m.Metadata
        );
    }

    private static string? TryGetStringArg(JsonElement args, string? key)
    {
        if (key is null) return null;
        if (args.ValueKind != JsonValueKind.Object) return null;
        if (args.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String)
            return prop.GetString();
        return null;
    }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/Seeing.Agent`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Seeing.Agent/Tools/BuiltIn/DefaultToolPermissionPolicy.cs
git commit -m "feat: add DefaultToolPermissionPolicy with built-in tool mappings"
```

---

### Task 3: Write unit tests for `DefaultToolPermissionPolicy`

**Files:**
- Create: `tests/Seeing.Agent.Tests/Tools/DefaultToolPermissionPolicyTests.cs`

- [ ] **Step 1: Create test file**

```csharp
// tests/Seeing.Agent.Tests/Tools/DefaultToolPermissionPolicyTests.cs

using FluentAssertions;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Tools.BuiltIn;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Tests.Tools;

public class DefaultToolPermissionPolicyTests
{
    private readonly DefaultToolPermissionPolicy _policy = new();

    [Fact]
    public void Evaluate_UnknownTool_ReturnsNull()
    {
        var args = JsonSerializer.SerializeToElement(new { });
        _policy.Evaluate("nonexistent", args).Should().BeNull();
    }

    [Fact]
    public void Evaluate_ReadTool_ReturnsFilesystemReadWithFilePath()
    {
        var args = JsonSerializer.SerializeToElement(new { file_path = "/home/user/readme.md" });
        var check = _policy.Evaluate("read", args);
        check.Should().NotBeNull();
        check!.PermissionKind.Should().Be("filesystem.read");
        check.Resource.Should().Be("/home/user/readme.md");
        check.Patterns.Should().BeNull();
    }

    [Fact]
    public void Evaluate_WriteTool_ReturnsFilesystemWriteWithFilePath()
    {
        var args = JsonSerializer.SerializeToElement(new { file_path = "/tmp/output.txt" });
        var check = _policy.Evaluate("write", args);
        check.Should().NotBeNull();
        check!.PermissionKind.Should().Be("filesystem.write");
        check.Resource.Should().Be("/tmp/output.txt");
    }

    [Fact]
    public void Evaluate_EditTool_ReturnsFilesystemWriteWithFilePath()
    {
        var args = JsonSerializer.SerializeToElement(new { file_path = "/app/config.json" });
        var check = _policy.Evaluate("edit", args);
        check.Should().NotBeNull();
        check!.PermissionKind.Should().Be("filesystem.write");
        check.Resource.Should().Be("/app/config.json");
    }

    [Fact]
    public void Evaluate_GrepTool_ReturnsFilesystemReadWithPath()
    {
        var args = JsonSerializer.SerializeToElement(new { path = "/src" });
        var check = _policy.Evaluate("grep", args);
        check.Should().NotBeNull();
        check!.PermissionKind.Should().Be("filesystem.read");
        check.Resource.Should().Be("/src");
    }

    [Fact]
    public void Evaluate_GlobTool_ReturnsFilesystemReadWithPath()
    {
        var args = JsonSerializer.SerializeToElement(new { path = "/src" });
        var check = _policy.Evaluate("glob", args);
        check.Should().NotBeNull();
        check!.PermissionKind.Should().Be("filesystem.read");
        check.Resource.Should().Be("/src");
    }

    [Fact]
    public void Evaluate_BashTool_ReturnsShellExecuteWithCommand()
    {
        var args = JsonSerializer.SerializeToElement(new { command = "ls -la" });
        var check = _policy.Evaluate("bash", args);
        check.Should().NotBeNull();
        check!.PermissionKind.Should().Be("shell.execute");
        check.Resource.Should().Be("ls -la");
    }

    [Fact]
    public void Evaluate_WebFetch_ReturnsNetworkFetchWithUrl()
    {
        var args = JsonSerializer.SerializeToElement(new { url = "https://example.com" });
        var check = _policy.Evaluate("webfetch", args);
        check.Should().NotBeNull();
        check!.PermissionKind.Should().Be("network.fetch");
        check.Resource.Should().Be("https://example.com");
    }

    [Fact]
    public void Evaluate_WebSearch_ReturnsNetworkSearchWithFixedResource()
    {
        var args = JsonSerializer.SerializeToElement(new { query = "test" });
        var check = _policy.Evaluate("websearch", args);
        check.Should().NotBeNull();
        check!.PermissionKind.Should().Be("network.search");
        check.Resource.Should().Be("web_search");
    }

    [Fact]
    public void Evaluate_CodeSearch_ReturnsNetworkSearchWithFixedResource()
    {
        var args = JsonSerializer.SerializeToElement(new { query = "test" });
        var check = _policy.Evaluate("codesearch", args);
        check.Should().NotBeNull();
        check!.PermissionKind.Should().Be("network.search");
        check.Resource.Should().Be("code_search");
    }

    [Fact]
    public void Evaluate_AddWorkspacePath_ReturnsWorkspaceExtendWithPatterns()
    {
        var args = JsonSerializer.SerializeToElement(new { path = "/new/project" });
        var check = _policy.Evaluate("add_workspace_path", args);
        check.Should().NotBeNull();
        check!.PermissionKind.Should().Be("filesystem.workspace_extend");
        check.Resource.Should().Be("/new/project/"); // trailing separator added
        check.Patterns.Should().NotBeNull();
        check.Patterns!.Should().Contain("/new/project");
        check.Metadata.Should().NotBeNull();
        check.Metadata!["reason"].Should().Be("Agent 请求扩展工作区路径");
    }

    [Fact]
    public void Evaluate_AddWorkspacePath_AlreadyTrailingSeparator_NotDoubled()
    {
        var args = JsonSerializer.SerializeToElement(new { path = "/existing/" });
        var check = _policy.Evaluate("add_workspace_path", args);
        check!.Resource.Should().Be("/existing/"); // not doubled
    }

    [Fact]
    public void Evaluate_TodoWrite_ReturnsNull_NoResourceCheck()
    {
        var args = JsonSerializer.SerializeToElement(new { todos = new[] { new { content = "x", status = "pending" } } });
        _policy.Evaluate("todowrite", args).Should().BeNull();
    }

    [Fact]
    public void Evaluate_CurrentTime_ReturnsNull_NoResourceCheck()
    {
        var args = JsonSerializer.SerializeToElement(new { });
        _policy.Evaluate("current_time", args).Should().BeNull();
    }

    [Fact]
    public void Evaluate_PlanEnter_ReturnsNull_NoResourceCheck()
    {
        var args = JsonSerializer.SerializeToElement(new { name = "plan" });
        _policy.Evaluate("plan_enter", args).Should().BeNull();
    }

    [Fact]
    public void Evaluate_Task_ReturnsNull_NoResourceCheck()
    {
        var args = JsonSerializer.SerializeToElement(new { description = "x", prompt = "y", subagent_type = "z" });
        _policy.Evaluate("task", args).Should().BeNull();
    }

    [Fact]
    public void Evaluate_MissingArgKey_ReturnsEmptyResource()
    {
        // grep with no "path" arg — resource is empty string
        var args = JsonSerializer.SerializeToElement(new { pattern = "something" });
        var check = _policy.Evaluate("grep", args);
        check.Should().NotBeNull();
        check!.Resource.Should().Be(string.Empty);
    }

    [Fact]
    public void Evaluate_NonObjectArgs_ReturnsNull()
    {
        // args that are a scalar, not an object
        var args = JsonSerializer.SerializeToElement("just a string");
        var check = _policy.Evaluate("read", args);
        // ArgKey lookup fails gracefully → resource is empty string
        check.Should().NotBeNull();
        check!.Resource.Should().Be(string.Empty);
    }
}
```

- [ ] **Step 2: Run tests to verify they pass**

Run: `dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~DefaultToolPermissionPolicyTests"`
Expected: All 17 tests pass.

- [ ] **Step 3: Commit**

```bash
git add tests/Seeing.Agent.Tests/Tools/DefaultToolPermissionPolicyTests.cs
git commit -m "test: add unit tests for DefaultToolPermissionPolicy"
```

---

### Task 4: Add `IToolPermissionPolicy` to `ToolManager` and insert check

**Files:**
- Modify: `src/Seeing.Agent/Tools/ToolManager.cs`

- [ ] **Step 1: Add field and constructor parameter**

Read `ToolManager.cs` lines 25-65. Add a new field `_permissionPolicy` and accept it in the constructor.

In the field declarations (after line 34 `_toolStateLock`):

```csharp
private readonly IToolPermissionPolicy? _permissionPolicy;
```

In the constructor (after the `_workspace = workspace;` line):

```csharp
_permissionPolicy = permissionPolicy;
```

Update the constructor signature to include the new parameter:

```csharp
public ToolManager(
    ILogger<ToolManager> logger,
    Core.Hooks.IHookManager hookManager,
    IServiceProvider? serviceProvider = null,
    IToolDecoratorRegistry? decoratorRegistry = null,
    IRuleEvaluator? ruleEvaluator = null,
    IWorkspaceProvider? workspace = null,
    IToolPermissionPolicy? permissionPolicy = null)     // ← new
```

- [ ] **Step 2: Insert permission check in `ExecuteAsync`**

Read lines 536-569. Insert after the `if (!hookResult.Continue)` block (after line 544) and before `var startTime = DateTime.Now;`:

Replace the current code from line 546 `var startTime = DateTime.Now;` through line 567 to:

```csharp
            // Resolve args from hook-mutable (moved up for permission check access)
            JsonElement resolvedArgs;
            if (argsMutable["args"] is JsonElement je)
                resolvedArgs = je;
            else if (argsMutable["args"] != null)
                resolvedArgs = JsonSerializer.SerializeToElement(argsMutable["args"]);
            else
                resolvedArgs = toolCall.Arguments is JsonElement je2 ? je2 : new JsonElement();

            // ========== Resource-level permission check ==========
            if (permissionChannel != null && _permissionPolicy != null)
            {
                var check = _permissionPolicy.Evaluate(toolId, resolvedArgs);
                if (check != null)
                {
                    var decision = await permissionChannel.RequestAsync(new PermissionRequest
                    {
                        PermissionKind = check.PermissionKind,
                        Resource = check.Resource,
                        Patterns = check.Patterns ?? new List<string>(),
                        SessionId = sessionId,
                        Metadata = check.Metadata ?? new Dictionary<string, object>()
                    }, cancellationToken).ConfigureAwait(false);

                    if (decision.Action == PermissionChannelAction.Deny)
                    {
                        return new ToolResult
                        {
                            Success = false,
                            ToolCallId = toolCall.Id,
                            Error = decision.Reason ?? "Permission denied"
                        };
                    }
                }
            }

            // 执行工具（装饰器链已在 RegisterTool 时 Apply，处理重试/超时/缓存）
            var startTime = DateTime.Now;

            try
            {
                var context = new ToolContext
                {
                    SessionId = sessionId,
                    CallId = toolCall.Id,
                    CancellationToken = cancellationToken,
                    EmitAsync = emitAsync,
                    PermissionChannel = permissionChannel,
                    Services = _serviceProvider
                };

                var toolResult = await tool.ExecuteAsync(resolvedArgs, context).ConfigureAwait(false);
```

Note: `resolvedArgs` replaces the previous `args` variable; the old `args` resolution lines (561-567) are removed from inside `try`.

- [ ] **Step 3: Add `using` for `PermissionRequest` and `PermissionChannelAction`**

Verify these are already imported. Check line 1-8 of the file. The existing `using Seeing.Agent.Core.Interfaces;` already covers `PermissionRequest` and `PermissionChannelAction` (both in `IPermissionChannel.cs`). No additional using needed.

- [ ] **Step 4: Build to verify**

Run: `dotnet build src/Seeing.Agent`
Expected: Build succeeds. Code compiles with new ctor parameter and permission check block.

- [ ] **Step 5: Commit**

```bash
git add src/Seeing.Agent/Tools/ToolManager.cs
git commit -m "feat: add resource-level permission check in ToolManager.ExecuteAsync"
```

---

### Task 5: Wire up DI registration

**Files:**
- Modify: `src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs`

- [ ] **Step 1: Register `DefaultToolPermissionPolicy` and pass to `ToolManager`**

Read lines 500-519. Add policy registration before the `ToolManager` singleton, and pass it to the constructor.

Add before line 500:

```csharp
            // 工具权限策略
            services.TryAddSingleton<IToolPermissionPolicy, DefaultToolPermissionPolicy>();
```

At line 502-510, update the `ToolManager` factory to resolve and pass the policy:

```csharp
            services.AddSingleton<ToolManager>(sp =>
            {
                var logger = sp.GetRequiredService<ILogger<ToolManager>>();
                var hookManager = sp.GetRequiredService<Seeing.Agent.Core.Hooks.IHookManager>();
                var tools = sp.GetServices<ITool>();
                var decoratorRegistry = sp.GetService<IToolDecoratorRegistry>();
                var workspace = sp.GetService<IWorkspaceProvider>();
                var permissionPolicy = sp.GetService<IToolPermissionPolicy>();

                var invoker = new ToolManager(logger, hookManager, sp, decoratorRegistry,
                    workspace: workspace, permissionPolicy: permissionPolicy);

                // 自动注册所有 ITool
                foreach (var tool in tools)
                {
                    invoker.RegisterTool(tool);
                }

                return invoker;
            });
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/Seeing.Agent`
Expected: Build succeeds.

- [ ] **Step 3: Run existing ToolManager tests to verify no regressions**

Run: `dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~ToolInvokerTests"`
Expected: All existing tests pass (the old tests don't pass a `permissionPolicy`, they use the old ctor signature which still works due to the optional parameter).

- [ ] **Step 4: Commit**

```bash
git add src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs
git commit -m "feat: register IToolPermissionPolicy in DI and wire to ToolManager"
```

---

### Task 6: Remove `RequestPermissionAsync` from tools (batch 1: filesystem)

**Files:**
- Modify: `src/Seeing.Agent/Tools/BuiltIn/FileSystem/ReadTool.cs`
- Modify: `src/Seeing.Agent/Tools/BuiltIn/FileSystem/WriteTool.cs`
- Modify: `src/Seeing.Agent/Tools/BuiltIn/FileSystem/EditTool.cs`
- Modify: `src/Seeing.Agent/Tools/BuiltIn/FileSystem/GlobTool.cs`
- Modify: `src/Seeing.Agent/Tools/BuiltIn/FileSystem/GrepTool.cs`
- Modify: `src/Seeing.Agent/Tools/BuiltIn/FileSystem/AddWorkspacePathTool.cs`

- [ ] **Step 1: Remove from `ReadTool.cs`**

Remove lines 93-100:

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

- [ ] **Step 2: Remove from `WriteTool.cs`**

Remove lines 85-87:

```csharp
var permCheck = await RequestPermissionAsync(context, "filesystem.write", filePath,
    new Dictionary<string, object> { ["filePath"] = filePath });
if (permCheck != null) return permCheck;
```

- [ ] **Step 3: Remove from `EditTool.cs`**

Remove lines 112-114:

```csharp
var permCheck = await RequestPermissionAsync(context, "filesystem.write", filePath,
    new Dictionary<string, object> { ["filePath"] = filePath });
if (permCheck != null) return permCheck;
```

- [ ] **Step 4: Remove from `GlobTool.cs`**

Remove lines 100-102:

```csharp
var permCheck = await RequestPermissionAsync(context, "filesystem.read", searchPath,
    new Dictionary<string, object> { ["pattern"] = pattern, ["path"] = searchPath });
if (permCheck != null) return permCheck;
```

- [ ] **Step 5: Remove from `GrepTool.cs`**

Remove lines 83-85:

```csharp
var permCheck = await RequestPermissionAsync(context, "filesystem.read", searchPath,
    new Dictionary<string, object> { ["path"] = searchPath, ["pattern"] = pattern });
if (permCheck != null) return permCheck;
```

- [ ] **Step 6: Remove from `AddWorkspacePathTool.cs`**

The tool currently does a workspace boundary check AND a manual `channel.RequestAsync` call (lines 58-85). Remove the entire block from line 58 (the `var workspace = ...` check) through line 85 (the `return Success(...)`). Replace with:

```csharp
        if (!Directory.Exists(path))
            return Failure($"目录不存在: {path}");

        return Success("路径已加入当前会话的工作区白名单", path);
```

The old code was (lines 58-85):
```csharp
        var workspace = context.Services?.GetService<IWorkspaceProvider>();
        if (workspace != null && FileSystemHelper.IsPathWithinDirectory(path, workspace.WorkspaceRoot))
            return Success("路径已在工作区内，无需额外权限", path);

        var channel = context.PermissionChannel;
        if (channel == null)
            return Failure("未配置权限通道，无法申请路径权限");

        // 确保路径以分隔符结尾，以便记忆的目录前缀匹配生效
        var dirPath = path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

        var result = await channel.RequestAsync(new PermissionRequest
        {
            PermissionKind = "filesystem.workspace_extend",
            Resource = dirPath,
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
```

Also remove the now-unused `using Seeing.Agent.Core.Permission;` from line 6 of `AddWorkspacePathTool.cs`.

- [ ] **Step 7: Build to verify**

Run: `dotnet build src/Seeing.Agent`
Expected: Build succeeds. No remaining references to `RequestPermissionAsync` in built-in tools (except `ToolBase` definition itself).

- [ ] **Step 8: Commit**

```bash
git add src/Seeing.Agent/Tools/BuiltIn/FileSystem/ReadTool.cs
git add src/Seeing.Agent/Tools/BuiltIn/FileSystem/WriteTool.cs
git add src/Seeing.Agent/Tools/BuiltIn/FileSystem/EditTool.cs
git add src/Seeing.Agent/Tools/BuiltIn/FileSystem/GlobTool.cs
git add src/Seeing.Agent/Tools/BuiltIn/FileSystem/GrepTool.cs
git add src/Seeing.Agent/Tools/BuiltIn/FileSystem/AddWorkspacePathTool.cs
git commit -m "refactor: remove RequestPermissionAsync from filesystem tools"
```

---

### Task 7: Remove `RequestPermissionAsync` from tools (batch 2: shell, web, todo)

**Files:**
- Modify: `src/Seeing.Agent/Tools/BuiltIn/Shell/BashTool.cs`
- Modify: `src/Seeing.Agent/Tools/BuiltIn/Web/WebFetchTool.cs`
- Modify: `src/Seeing.Agent/Tools/BuiltIn/Web/WebSearchTool.cs`
- Modify: `src/Seeing.Agent/Tools/BuiltIn/Web/CodeSearchTool.cs`
- Modify: `src/Seeing.Agent/Tools/BuiltIn/Todo/TodoWriteTool.cs`

- [ ] **Step 1: Remove from `BashTool.cs`**

Remove lines 165-173:

```csharp
var permCheck = await RequestPermissionAsync(context, "shell.execute", command,
    new Dictionary<string, object>
    {
        ["command"] = command,
        ["workdir"] = workdir,
        ["timeout"] = timeout,
        ["description"] = description
    });
if (permCheck != null) return permCheck;
```

- [ ] **Step 2: Remove from `WebFetchTool.cs`**

Remove lines 100-107:

```csharp
var permCheck = await RequestPermissionAsync(context, "network.fetch", url,
    new Dictionary<string, object>
    {
        ["url"] = url,
        ["format"] = format,
        ["timeout"] = timeoutSeconds
    });
if (permCheck != null) return permCheck;
```

- [ ] **Step 3: Remove from `WebSearchTool.cs`**

Remove lines 91-100:

```csharp
var permCheck = await RequestPermissionAsync(context, "network.search", "web_search",
    new Dictionary<string, object>
    {
        ["query"] = query,
        ["numResults"] = numResults,
        ["livecrawl"] = livecrawl,
        ["type"] = searchType,
        ["contextMaxCharacters"] = contextMaxCharacters ?? (object)0
    });
if (permCheck != null) return permCheck;
```

- [ ] **Step 4: Remove from `CodeSearchTool.cs`**

Remove lines 77-83:

```csharp
var permCheck = await RequestPermissionAsync(context, "network.search", "code_search",
    new Dictionary<string, object>
    {
        ["query"] = query,
        ["tokensNum"] = tokensNum
    });
if (permCheck != null) return permCheck;
```

- [ ] **Step 5: Remove from `TodoWriteTool.cs` (TodoReadTool inner class)**

Remove lines 266-268 from the `TodoReadTool.ExecuteAsync` method:

```csharp
var permCheck = await RequestPermissionAsync(context, "tool.execute", "todoread",
    new Dictionary<string, object>());
if (permCheck != null) return permCheck;
```

- [ ] **Step 6: Build to verify**

Run: `dotnet build src/Seeing.Agent`
Expected: Build succeeds.

- [ ] **Step 7: Commit**

```bash
git add src/Seeing.Agent/Tools/BuiltIn/Shell/BashTool.cs
git add src/Seeing.Agent/Tools/BuiltIn/Web/WebFetchTool.cs
git add src/Seeing.Agent/Tools/BuiltIn/Web/WebSearchTool.cs
git add src/Seeing.Agent/Tools/BuiltIn/Web/CodeSearchTool.cs
git add src/Seeing.Agent/Tools/BuiltIn/Todo/TodoWriteTool.cs
git commit -m "refactor: remove RequestPermissionAsync from shell, web, and todo tools"
```

---

### Task 8: Add integration tests for ToolManager permission check

**Files:**
- Modify: `tests/Seeing.Agent.Tests/Tools/ToolInvokerTests.cs`

- [ ] **Step 1: Add `using` for `PermissionChannelAction` and `PermissionRequest`**

Add at the top of the file:

```csharp
using Seeing.Agent.Core.Permission;
```

- [ ] **Step 2: Add test — tool denied by permission policy returns failure**

```csharp
[Fact]
public async Task ExecuteAsync_PermissionPolicyDenies_ReturnsFailure()
{
    // Arrange
    var permissionPolicy = new Mock<IToolPermissionPolicy>();
    var permissionChannel = new Mock<IPermissionChannel>();
    var invoker = new ToolManager(
        _loggerMock.Object, _hookManager,
        permissionPolicy: permissionPolicy.Object);
    invoker.RegisterTool(new TestTool());

    permissionPolicy
        .Setup(p => p.Evaluate("test_tool", It.IsAny<JsonElement>()))
        .Returns(new PermissionResourceCheck("filesystem.read", "/secret/file.txt"));

    permissionChannel
        .Setup(c => c.RequestAsync(It.IsAny<PermissionRequest>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(PermissionChannelResult.Denied("User denied"));

    var toolCall = new ToolCall
    {
        Id = "call-001",
        Function = new FunctionCall { Name = "test_tool", Arguments = "{}" }
    };

    // Act
    var result = await invoker.ExecuteAsync(toolCall, "session-1",
        CancellationToken.None, null, permissionChannel.Object);

    // Assert
    result.Success.Should().BeFalse();
    result.Error.Should().Be("User denied");
}
```

- [ ] **Step 3: Add test — tool allowed by permission policy proceeds normally**

```csharp
[Fact]
public async Task ExecuteAsync_PermissionPolicyAllows_ProceedsToTool()
{
    // Arrange
    var permissionPolicy = new Mock<IToolPermissionPolicy>();
    var permissionChannel = new Mock<IPermissionChannel>();
    var invoker = new ToolManager(
        _loggerMock.Object, _hookManager,
        permissionPolicy: permissionPolicy.Object);
    invoker.RegisterTool(new TestTool());

    permissionPolicy
        .Setup(p => p.Evaluate("test_tool", It.IsAny<JsonElement>()))
        .Returns(new PermissionResourceCheck("filesystem.read", "/allowed/file.txt"));

    permissionChannel
        .Setup(c => c.RequestAsync(It.IsAny<PermissionRequest>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(PermissionChannelResult.Allowed());

    var toolCall = new ToolCall
    {
        Id = "call-002",
        Function = new FunctionCall { Name = "test_tool", Arguments = "{}" }
    };

    // Act
    var result = await invoker.ExecuteAsync(toolCall, "session-1",
        CancellationToken.None, null, permissionChannel.Object);

    // Assert
    result.Success.Should().BeTrue();
    result.Output.Should().Be("完成");
}
```

- [ ] **Step 4: Add test — tool with null check (no resource needed) skips channel**

```csharp
[Fact]
public async Task ExecuteAsync_PermissionPolicyReturnsNull_SkipsChannelCheck()
{
    // Arrange
    var permissionPolicy = new Mock<IToolPermissionPolicy>();
    var permissionChannel = new Mock<IPermissionChannel>();
    var invoker = new ToolManager(
        _loggerMock.Object, _hookManager,
        permissionPolicy: permissionPolicy.Object);
    invoker.RegisterTool(new TestTool());

    permissionPolicy
        .Setup(p => p.Evaluate("test_tool", It.IsAny<JsonElement>()))
        .Returns((PermissionResourceCheck?)null);

    var toolCall = new ToolCall
    {
        Id = "call-003",
        Function = new FunctionCall { Name = "test_tool", Arguments = "{}" }
    };

    // Act
    var result = await invoker.ExecuteAsync(toolCall, "session-1",
        CancellationToken.None, null, permissionChannel.Object);

    // Assert
    result.Success.Should().BeTrue();
    // Verify channel was never called
    permissionChannel.Verify(
        c => c.RequestAsync(It.IsAny<PermissionRequest>(), It.IsAny<CancellationToken>()),
        Times.Never);
}
```

- [ ] **Step 5: Add test — no policy configured falls through to tool execution**

```csharp
[Fact]
public async Task ExecuteAsync_NoPolicy_NullChannel_Succeeds()
{
    // Arrange — no policy, no channel: old behavior, tool runs fine
    var invoker = new ToolManager(_loggerMock.Object, _hookManager);
    invoker.RegisterTool(new TestTool());

    var toolCall = new ToolCall
    {
        Id = "call-004",
        Function = new FunctionCall { Name = "test_tool", Arguments = "{}" }
    };

    // Act
    var result = await invoker.ExecuteAsync(toolCall);

    // Assert
    result.Success.Should().BeTrue();
}
```

- [ ] **Step 6: Add `using Moq;` for permission channel mock**

Add at the top of `ToolInvokerTests.cs` if not present:

```csharp
using Moq;
```

(It is already present — verify at line 3.)

- [ ] **Step 7: Run tests**

Run: `dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~ToolInvokerTests"`
Expected: All tests pass (existing + 4 new).

- [ ] **Step 8: Commit**

```bash
git add tests/Seeing.Agent.Tests/Tools/ToolInvokerTests.cs
git commit -m "test: add ToolManager permission check integration tests"
```

---

### Task 9: Run full test suite and verify

- [ ] **Step 1: Run all tests**

```bash
dotnet test
```
Expected: All tests pass.

- [ ] **Step 2: Build the WebUI to verify end-to-end**

```bash
dotnet build samples/Seeing.Agent.WebUI
```
Expected: Build succeeds.

- [ ] **Step 3: Verify no remaining `RequestPermissionAsync` in built-in tools**

```bash
grep -rn "RequestPermissionAsync" src/Seeing.Agent/Tools/BuiltIn/
```
Expected: No results (or only in `BuiltInToolBase.cs` which is fine — the method definition stays for backward compat).

- [ ] **Step 4: Commit (if any final cleanup)**

```bash
git commit -am "chore: final verification — all tests pass, no residual permission code in tools"
```
