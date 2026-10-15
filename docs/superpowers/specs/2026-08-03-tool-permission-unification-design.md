# Unified Tool Resource Permission Design

2026-08-03 — Centralize resource-level permission checks in `ToolManager`

## Problem

Built-in tools currently call `RequestPermissionAsync` individually, scattering
permission logic across ~11 files. This is inconsistent, duplicative, and
complicates auditing. Additionally, the checks happen *before* the Hook pipeline
can modify arguments, creating a potential gap.

## Current State

### Two-layer permission model

| Layer | Location | What it checks | Mechanism |
|-------|----------|---------------|-----------|
| Capability gate | `AgentExecutor.EvaluatePermissionAsync` (line 686) | "Can this agent call tool X?" | `PermissionService.EvaluateToolAsync` → Allow/Deny rules |
| Resource gate | Each tool's `ExecuteAsync` | "Can this tool operate on resource Y?" | `ToolBase.RequestPermissionAsync` → `PermissionChannel` → user prompt |

### Tools with internal permission requests (10 calls across 10 tools)

| Tool | PermissionKind | Resource |
|------|---------------|----------|
| `read` | filesystem.read | `file_path` arg |
| `grep` | filesystem.read | `path` arg |
| `glob` | filesystem.read | `path` arg |
| `write` | filesystem.write | `file_path` arg |
| `edit` | filesystem.write | `file_path` arg |
| `bash` | shell.execute | `command` arg |
| `webfetch` | network.fetch | `url` arg |
| `websearch` | network.search | `"web_search"` (fixed) |
| `codesearch` | network.search | `"code_search"` (fixed) |
| `add_workspace_path` | filesystem.workspace_extend | `path` arg |
| `todoread` | tool.execute / "todoread" | **REDUNDANT** — duplicates capability gate |

### Key architectural benefit of resource-level checks

`SerializingPermissionChannel` provides **workspace boundary auto-approval**
(filesystem ops inside workspace root never prompt) and **session-scoped
directory-prefix memory** — capabilities the pre-execution `PermissionService`
cannot provide since it only sees tool names, not arguments.

## Design

### Principle

**Capability gate stays in AgentExecutor; resource gate moves to ToolManager.**

AgentExecutor remains the single place that decides *whether an agent is allowed
to invoke a tool at all*. ToolManager becomes the single place that checks
*whether the tool may access a specific resource*. The tool itself carries zero
permission code.

### New execution flow

```
AgentExecutor
  ├── PermissionService.EvaluateToolAsync("read")   ← capability gate (unchanged)
  └── ToolManager.ExecuteAsync()
        ├── tool.execute.before Hook                 ← plugins may modify args
        ├── NEW: IToolPermissionPolicy.Evaluate()    ← resource gate (unified)
        │     └── PermissionChannel.RequestAsync()
        │           ├── workspace boundary → auto-approve
        │           ├── session memory match
        │           └── user prompt (if needed)
        └── tool.ExecuteAsync(args, ctx)              ← zero permission code
```

### New interface

```csharp
// src/Seeing.Agent/Core/Interfaces/IToolPermissionPolicy.cs

/// <summary>
/// Resource-level permission policy for tool invocations.
/// Evaluated AFTER tool.execute.before hooks, inside ToolManager.ExecuteAsync.
/// For tools that need no resource check, returns null (the common case).
/// </summary>
public interface IToolPermissionPolicy
{
    /// <summary>
    /// Returns a resource-level permission check for the given tool,
    /// or null to skip (tool needs no resource-level check).
    /// Called after tool.execute.before hooks have run.
    /// </summary>
    PermissionResourceCheck? Evaluate(
        string toolId, JsonElement args);
}

public record PermissionResourceCheck(
    string PermissionKind,
    string Resource,
    List<string>? Patterns = null,
    Dictionary<string, object>? Metadata = null
);
```

### Default implementation: `DefaultToolPermissionPolicy`

Maps built-in tool IDs to their resource parameters via a static table:

```csharp
// Internal helper struct
internal readonly record struct ToolPermissionMapping(
    string PermissionKind,
    string? ArgKey,                    // which JsonElement arg field holds the resource
    string? FixedResource = null,      // override: use this literal instead of arg value
    bool TrailingDirSeparator = false, // append '/' to resource (for dir-prefix memory)
    string? PatternsArgKey = null,     // which arg to also pass as Patterns
    Dictionary<string, object>? Metadata = null
);

private static readonly Dictionary<string, ToolPermissionMapping> Mappings = new()
{
    ["read"]      = new("filesystem.read",      "file_path"),
    ["write"]     = new("filesystem.write",     "file_path"),
    ["edit"]      = new("filesystem.write",     "file_path"),
    ["grep"]      = new("filesystem.read",      "path"),
    ["glob"]      = new("filesystem.read",      "path"),
    ["bash"]      = new("shell.execute",        "command"),
    ["webfetch"]  = new("network.fetch",        "url"),
    ["websearch"] = new("network.search",       null, FixedResource: "web_search"),
    ["codesearch"]= new("network.search",       null, FixedResource: "code_search"),
    ["add_workspace_path"] = new("filesystem.workspace_extend", "path",
                          TrailingDirSeparator: true,
                          PatternsArgKey: "path",
                          Metadata: new() { ["reason"] = "Agent 请求扩展工作区路径" }),
};
```

The `ToolPermissionMapping` is internal — only the `IToolPermissionPolicy`
interface is public. Third-party implementations provide their own mapping logic.

For tools not in the table, `Evaluate` returns `null` (no check needed).
This covers all current tools (`plan_*`, `task`, `task_status`, `todowrite`,
`todoread`, `current_time`, etc.) — they remain untouched.

### Insertion point in `ToolManager.ExecuteAsync`

```csharp
// After: tool.execute.before hook (line 524–544)

// Resolve args from hook-mutable (was inside try; moved up so permission check can use it)
JsonElement resolvedArgs;
if (argsMutable["args"] is JsonElement je)
    resolvedArgs = je;
else if (argsMutable["args"] != null)
    resolvedArgs = JsonSerializer.SerializeToElement(argsMutable["args"]);
else
    resolvedArgs = toolCall.Arguments is JsonElement je2 ? je2 : new JsonElement();

// --- Resource-level permission check (NEW) ---
if (permissionChannel != null)
{
    var check = _permissionPolicy.Evaluate(toolId, resolvedArgs);
    if (check != null)
    {
        var decision = await permissionChannel.RequestAsync(new PermissionRequest
        {
            PermissionKind = check.PermissionKind,
            Resource = check.Resource,
            Patterns = check.Patterns ?? new(),
            SessionId = sessionId,
            Metadata = check.Metadata ?? new()
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

// Then: existing try block — ToolContext, tool.ExecuteAsync(resolvedArgs, context)
```

Note: `args` resolution (lines 561–567) moves from inside the `try` block to
before the permission check, since both the check and the tool need the
hook-modified value. The `try` block now receives `resolvedArgs`.

### Tool-side changes

Remove all `RequestPermissionAsync` calls from tools:

| File | What to remove |
|------|---------------|
| `ReadTool.cs:93` | `RequestPermissionAsync(context, "filesystem.read", filePath, …)` |
| `GrepTool.cs:83` | `RequestPermissionAsync(context, "filesystem.read", searchPath, …)` |
| `GlobTool.cs:100` | `RequestPermissionAsync(context, "filesystem.read", searchPath, …)` |
| `WriteTool.cs:85` | `RequestPermissionAsync(context, "filesystem.write", filePath, …)` |
| `EditTool.cs:112` | `RequestPermissionAsync(context, "filesystem.write", filePath, …)` |
| `BashTool.cs:165` | `RequestPermissionAsync(context, "shell.execute", command, …)` |
| `WebFetchTool.cs:100` | `RequestPermissionAsync(context, "network.fetch", url, …)` |
| `WebSearchTool.cs:91` | `RequestPermissionAsync(context, "network.search", "web_search", …)` |
| `CodeSearchTool.cs:77` | `RequestPermissionAsync(context, "network.search", "code_search", …)` |
| `TodoWriteTool.cs:266` | `RequestPermissionAsync(context, "tool.execute", "todoread", …)` (TodoReadTool) |
| `AddWorkspacePathTool.cs:62-83` | Direct `channel.RequestAsync(…)` call — delegated to policy |

After removal, `AddWorkspacePathTool` keeps only its directory existence check.

The `ToolBase.RequestPermissionAsync` protected method is **not removed** —
third-party tools via MCP or plugins may still use it. But it is no longer
called by any built-in tool.

### DI registration

```csharp
// In SeeingAgentServiceCollectionExtensions or ToolManager registration
services.TryAddSingleton<IToolPermissionPolicy, DefaultToolPermissionPolicy>();
```

`ToolManager` constructor gains one parameter:

```csharp
public ToolManager(
    ...,
    IToolPermissionPolicy? permissionPolicy = null)
```

### Exclusions and non-goals

1. **AgentExecutor capability gate is unchanged.** It continues to evaluate
   `PermissionKind.Tool` rules to decide whether an agent is permitted to call a
   tool at all.

2. **PermissionChannel, SerializingPermissionChannel, and IPermissionMemory are
   unchanged.** Only the caller changes (from tool code → ToolManager).

3. **MCP tools** are not affected. They use a separate path (MCP server name +
   tool name) not covered by `IToolPermissionPolicy`.

4. **`ToolBase.RequestPermissionAsync`** stays for backward compatibility with
   non-built-in tools and plugins.

## Migration plan

1. Create `IToolPermissionPolicy` and `DefaultToolPermissionPolicy`
2. Add constructor parameter to `ToolManager`, register in DI
3. Insert permission check in `ToolManager.ExecuteAsync` after hook, before execution
4. Remove `RequestPermissionAsync` from all 10 tool files (11 calls)
5. Update `AddWorkspacePathTool` to remove the manual `PermissionChannel` call
6. Run existing tests, add new tests for `DefaultToolPermissionPolicy`
7. Update `CLAUDE.md` to document the new permission architecture
