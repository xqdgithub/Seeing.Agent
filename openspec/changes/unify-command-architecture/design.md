## Context

The current command system has evolved with duplicated implementations:

```
Frontend (WebUI)                    Backend (Core)
────────────────                    ──────────────
BuiltInCommands.cs ─── 定义命令列表    BuiltInCommands.cs ─── 实现命令逻辑
    (硬编码15个命令)                      (仅实现5个命令)
                                        
CustomCommandLoader.cs ─ 加载.md     CommandDiscovery.cs ─ 加载.md
    (前端加载)                            (后端加载)
```

**Key Problems:**
1. Frontend defines commands that don't exist in backend (`/model`, `/agent`, `/mcp`, `/undo`, `/redo`, `/share`, `/fork`, `/terminal`, `/init`)
2. Two separate command discovery mechanisms create potential sync issues
3. Command list changes require updating both frontend and backend

**Stakeholders:**
- `ICommandRegistry` - Backend single source of truth for commands
- `CommandDispatcher` - Backend command execution
- `PromptInput.razor` - Frontend command autocomplete
- `Session.razor` - Frontend command orchestration

## Goals / Non-Goals

**Goals:**
- Single source of truth for command definitions (backend `ICommandRegistry`)
- Frontend fetches command list from backend via DI
- Implement missing backend commands (`/agent`, `/model`, `/mcp`, `/tools`, `/fork`)
- Remove frontend command definition files

**Non-Goals:**
- Implementing `/terminal` (removed - use UI button instead)
- Implementing `/init` (complex project initialization, separate change)
- Implementing `/undo`, `/redo`, `/share` (deferred - requires additional design)
- Changing the command execution flow or `CommandDispatcher` logic

## Decisions

### Decision 1: Frontend Fetches Commands via DI

**Choice:** Inject `ICommandRegistry` into Blazor components and call `GetAllMetadata()` directly.

**Alternatives Considered:**
- **API Endpoint**: Create REST API to expose command list
  - Rejected: Overkill for Blazor Server where backend services are directly accessible
- **SignalR Push**: Push command updates to frontend
  - Rejected: Commands rarely change; polling/direct access sufficient

**Rationale:** Blazor Server shares memory with backend; direct DI is simplest and most efficient.

### Decision 2: New Command Providers by Domain

**Choice:** Create separate command provider classes by functional domain:
- `SessionCommands` - Session management (`/undo`, `/share`, `/fork`)
- `AgentCommands` - Agent/model switching (`/agent`, `/model`)
- `ToolsCommands` - Tool management (`/mcp`, `/tools`)

**Alternatives Considered:**
- **Single Commands.cs file**: All new commands in one file
  - Rejected: Poor separation of concerns, harder to maintain
- **Command per file**: Each command in its own file
  - Rejected: Too granular, many small files

**Rationale:** Domain grouping matches existing pattern (`BuiltInCommands`, `SkillCommands`) and keeps related commands together.

### Decision 3: CommandItemViewModel Mapping

**Choice:** Create mapping from `CommandMetadata` to `CommandItemViewModel` in frontend service.

**Alternatives Considered:**
- **Shared DTO**: Create shared `CommandDto` in contracts project
  - Rejected: Over-engineering; simple mapping sufficient
- **Direct use of CommandMetadata**: Use backend type in frontend
  - Rejected: `CommandMetadata` has frontend-irrelevant fields (ParametersSchema, Agent, Template)

**Rationale:** View models should be frontend-specific; mapping layer provides flexibility for UI concerns like icon mapping.

### Decision 4: Icon Mapping Strategy

**Choice:** Map icons by `CommandCategory` enum with static dictionary.

```csharp
private static readonly Dictionary<CommandCategory, string> CategoryIcons = new()
{
    [CommandCategory.Basic] = IconType.Outline.Home,
    [CommandCategory.Navigation] = IconType.Outline.Compass,
    [CommandCategory.Agent] = IconType.Outline.Robot,
    [CommandCategory.Tools] = IconType.Outline.Tool,
    [CommandCategory.System] = IconType.Outline.Setting,
};
```

**Rationale:** Simple, predictable mapping; icons are frontend concern and shouldn't be in backend metadata.

## Architecture

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                         Unified Command Architecture                         │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│   Backend                                    Frontend                       │
│   ───────                                    ────────                       │
│                                                                             │
│   ┌─────────────────┐                       ┌─────────────────────────┐   │
│   │ ICommandRegistry│                       │ CommandListService      │   │
│   │                 │◄────── DI ────────────│                         │   │
│   │ GetAllMetadata()│                       │ GetAvailableCommands()  │   │
│   └─────────────────┘                       └─────────────────────────┘   │
│           ▲                                            │                   │
│           │                                            │                   │
│    Register│                                            ▼                   │
│           │                                ┌─────────────────────────┐   │
│   ┌───────┴───────┬───────────────┐        │ PromptInput.razor       │   │
│   │               │               │        │                         │   │
│   ▼               ▼               ▼        │ AvailableCommands ──────┤   │
│ ┌────────┐ ┌──────────┐ ┌──────────┐       │ AvailableTools          │   │
│ │BuiltIn │ │SkillCmds │ │Session   │       │ AvailableSkills         │   │
│ │Commands│ │          │ │Commands  │       └─────────────────────────┘   │
│ └────────┘ └──────────┘ └──────────┘                                      │
│                    ┌──────────┐ ┌──────────┐                              │
│                    │Agent     │ │Tools     │                              │
│                    │Commands  │ │Commands  │                              │
│                    └──────────┘ └──────────┘                              │
│                                                                             │
│   DELETED:                                                                   │
│   - samples/.../Commands/BuiltInCommands.cs (frontend duplicate)           │
│   - samples/.../Commands/CustomCommandLoader.cs (frontend duplicate)       │
│                                                                             │
└─────────────────────────────────────────────────────────────────────────────┘
```

## Risks / Trade-offs

### Risk 1: Breaking Change for Custom Commands
**Risk:** Users with custom `.md` commands in `~/.seeing/commands/` might expect frontend-only loading.
**Mitigation:** Backend `CommandDiscovery.DiscoverFromMarkdownAsync()` already handles this; no user impact.

### Risk 2: Performance on Command List Fetch
**Risk:** Calling `GetAllMetadata()` on every render could impact performance.
**Mitigation:** Fetch once in `OnInitializedAsync()`, cache in component state. Commands rarely change during session.

### Risk 3: Incomplete Command Implementations
**Risk:** New commands (`/undo`, `/share`, `/fork`) depend on services that may not exist.
**Mitigation:** 
- `/undo` - Use existing `ISessionManager`
- `/share` - Requires `SessionSharer` (check if exists, return helpful error if not)
- `/fork` - Requires `SessionForker` (check if exists, return helpful error if not)

### Trade-off: No Real-time Command Updates
**Trade-off:** If commands are added dynamically, frontend won't see them until page refresh.
**Acceptable because:** Commands are statically defined; dynamic addition is rare. Users can refresh if needed.

## Migration Plan

### Phase 1: Add Backend Commands
1. Create `SessionCommands.cs`, `AgentCommands.cs`, `ToolsCommands.cs`
2. Register in `ChatOrchestratorServiceExtensions.cs`
3. Verify commands work via `ChatOrchestrator`

### Phase 2: Update Frontend
1. Create `CommandListService.cs` (frontend service)
2. Update `Session.razor` to use service
3. Update `PromptInput.razor` to accept commands as parameter

### Phase 3: Remove Duplicates
1. Delete `samples/.../Commands/BuiltInCommands.cs`
2. Delete `samples/.../Commands/CustomCommandLoader.cs`
3. Remove `using` statements from affected files

### Rollback Strategy
If issues arise, revert commits in reverse order:
1. Restore deleted files
2. Revert frontend changes
3. Remove new backend commands

## Service Dependencies (审查澄清)

### `/fork` 命令依赖

**实现方案:**
```csharp
// SessionCommands 中直接使用 ISessionManager
public class SessionCommands
{
    private readonly ISessionManager _sessionManager;

    /// <summary>/fork - 分叉会话</summary>
    public async Task<CommandResult> ForkSession(CommandContext context, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(context.SessionId))
            return CommandResult.Fail("No active session");

        var title = context.Arguments.Trim();
        var newSession = await _sessionManager.ForkAsync(context.SessionId, label: title, ct: ct);
        
        return CommandResult.Ok($"会话已分叉: {newSession.Id}")
            .WithNavigation($"/session/{newSession.Id}");
    }
}
```

**决策:** 直接使用 `ISessionManager.ForkAsync()` 方法。

### `/undo`, `/redo`, `/share` 延迟实现

**决策:** 暂不实现这三个命令，原因：
- `/undo` - 需要更优雅的撤销历史设计
- `/redo` - 依赖 undo 实现
- `/share` - 需要确认分享机制的完整设计

后续可单独变更实现。

### `/mcp` 和 `/tools` 命令依赖

**现状分析:**
- `ToolInvoker` 已注册，可通过 `GetToolSchemasAsync()` 获取工具列表
- MCP 工具以 `mcp_` 前缀命名，可通过过滤识别

**实现方案:**
```csharp
// ToolsCommands 注入 ToolInvoker
public class ToolsCommands
{
    private readonly ToolInvoker _toolInvoker;
    
    public async Task<CommandResult> ListMcpTools(...)
    {
        var schemas = await _toolInvoker.GetToolSchemasAsync();
        var mcpTools = schemas.Where(s => s.Function.Name.StartsWith("mcp_"));
        // ...
    }
}
```

## Frontend Command Sync (审查澄清)

### 前端定义但后端不实现的命令

| 命令 | 前端定义 | 后端实现 | 处理方式 |
|------|---------|---------|---------|
| `/terminal` | ✅ | ❌ | **删除** - 改用 UI 按钮触发终端切换 |
| `/init` | ✅ | ❌ | **删除** - Non-Goal，后续单独实现 |
| `/undo` | ✅ | ❌ | **删除** - 延迟实现，需更优雅设计 |
| `/redo` | ✅ | ❌ | **删除** - 延迟实现 |
| `/share` | ✅ | ❌ | **删除** - 延迟实现 |
| `/exit` | ❌ | ✅ | 从后端获取后自动可用 |
| `/skills` | ❌ | ✅ | 从后端获取后自动可用 |

### 处理策略

1. **`/terminal`**: 从前端删除，改用 UI 按钮触发终端视图切换
2. **`/undo`, `/redo`, `/share`**: 延迟实现，后续单独变更
3. **`/init`**: 从前端删除，后续单独变更实现
4. **`/exit`, `/skills`**: 从后端获取命令列表后自动可用

## CommandAvailabilityChecker Migration (审查澄清)

**现状:** `CommandAvailabilityChecker` 使用硬编码的命令名称检查可用性。

**决策:** 在 `CommandListService` 中实现相同逻辑：
- 需要 session 的命令（`/clear`, `/fork`, `/compact`）在无 session 时禁用
- 其他命令默认可用

```csharp
// CommandListService.cs
private bool CanExecute(string commandName, SessionState? session)
{
    var requiresSession = new[] { "clear", "fork", "compact" };
    return !requiresSession.Contains(commandName) || session?.CurrentSession != null;
}
```

## Markdown Command Conflict (审查澄清)

**当前行为：** 后端 `CommandRegistry.Register()` 会覆盖同名命令并输出警告日志：
```csharp
if (_commands.ContainsKey(name))
{
    _logger?.LogWarning("命令 '{Name}' 已存在，将被覆盖", name);
}
_commands[name] = command;  // 继续覆盖
```

**决策：** 保持当前行为（覆盖），不修改 `CommandRegistry` 逻辑。
- 用户定义的 `.md` 命令与内置命令同名时，会覆盖内置命令
- 警告日志会记录覆盖行为
- 前端不需要做冲突检测，以后端最终注册结果为准

## Icon Mapping (审查澄清)

**决策:** 使用静态字典映射分类到图标：
```csharp
Basic      → IconType.Outline.Home
Navigation → IconType.Outline.Compass
Agent      → IconType.Outline.Robot
Tools      → IconType.Outline.Tool
System     → IconType.Outline.Setting
Extension  → IconType.Outline.Appstore
Other      → IconType.Outline.Code
```
