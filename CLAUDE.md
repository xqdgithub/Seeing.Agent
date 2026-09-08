# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Test

```bash
# Build the entire solution (uses CPM, no restore needed for first build)
dotnet build

# Run all tests
dotnet test

# Run a specific test project (paths after src/tests categorization)
dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll

# Run a single test (VSTest filter)
dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~ClassName.TestMethod"

# Invariants
dotnet vstest tests/spine/Seeing.Agent.Invariants.Tests/bin/Debug/net10.0/Seeing.Agent.Invariants.Tests.dll

# Run the Blazor WebUI (primary dev app; also starts Gateway server on :8765)
dotnet run --project samples/Seeing.Agent.WebUI

# Run the headless Gateway Server (agent + gateway without UI)
dotnet run --project samples/Seeing.Gateway.Server
```

Target framework: **net10.0**. Package versions are managed centrally via `Directory.Packages.props` (CPM).

## Architecture

**Canonical docs:** [`docs/architecture/README.md`](docs/architecture/README.md) (overview, modules, config/lifecycle, standards, extension, compliance audit).  
**Design spec:** `docs/superpowers/specs/2026-09-08-modular-architecture-design.md`.

### Disk layout (assembly names unchanged)

```
src/primitives|abstractions|spine|hosting|capabilities|gateway/
tests/          mirrored (+ apps|plugs)
```

### Solution Layering

```
Seeing.Agent.Abstractions     ← contracts only   (src/abstractions)
Seeing.Agent.Core         ← spine             (src/spine)
Seeing.Agent.Hosting      ← ExecutionJobService, Task/Todo (src/hosting)
capability modules        ← Tools.*, Skills, Mcp… (src/capabilities)
Host Shape                ← Web/Headless/Embed/Gateway (src/hosting)
Gateway family            ← src/gateway
samples/WebUI             ← composes modules + UI
```

**Do not** put tools back into Core. **Do not** use `AddSeeingAgent`. Call `InitializeSeeingAsync` before `Host.StartAsync`. **Do not** add new projects flat under `src/`.

**Supporting libraries** (all target `net10.0`; capability packages must depend on Abstractions only — compliance status in `docs/architecture/06-compliance-audit.md`):

| Project | Purpose |
|---------|---------|
| `Seeing.Session` | Session/chat message storage (file-based), compression, management |
| `Seeing.Agent.Scheduler` | Cron job scheduling via Quartz.NET + SQLite persistence |
| `Seeing.Agent.Memory` | Vector + graph memory with hybrid retrieval, outputs to `~/.seeing/plugins/` |
| `Seeing.Agent.Acp` | Agent Client Protocol (Acp.NetCore) integration for agent-to-agent comm |
| `Seeing.Agent.TokenBudget` | Token usage tracking and budget enforcement via hooks |
| `Seeing.TokenEstimation` | Token counting utilities (dep of Session and TokenBudget) |

**Gateway family** (external communication to IM channels):

| Project | Purpose |
|---------|---------|
| `Seeing.Gateway` | Protocol DTOs, event mapping |
| `Seeing.Gateway.Client` | HTTP/SSE + WebSocket client SDK |
| `Seeing.Agent.Gateway` | Server plugin running independent Kestrel instance |
| `Seeing.Gateway.WeCom` / `.QQ` | Channel bridges for WeCom and QQ |
| `samples/Seeing.Gateway.ChannelHost` | Out-of-process channel host for gateway channels |

### Core Concepts (primarily under `src/spine/Seeing.Agent.Core/` + `src/abstractions/`)

- **`IAgent`** (`Core/Interfaces/IAgent.cs`) — All agents implement this. Has metadata (Name, Mode, SystemPrompt, Model, PermissionRules) and `ExecuteAsync` returning `IAsyncEnumerable<ChatMessage>`.
- **`ITool`** (`Core/Interfaces/ITool.cs`) — Tools implement `Id`, `Description`, `ParametersSchema` (JSON Schema), and `ExecuteAsync(JsonElement arguments, ToolContext context)`. Also supports `[Tool]`/`[ToolParam]` attribute-based discovery.
- **`IHookHandler`** (`Core/Hooks/IHookHandler.cs`) — Register handlers for 25+ lifecycle hook points (`tool.execute.before`, `chat.params`, `session.compacting`, etc.). HookManager resolves by HookPoint string.
- **`IPermissionChannel`** (`Core/Interfaces/IPermissionChannel.cs`) — Pluggable permission confirmation. Default is `DefaultPermissionChannel` (throws unless `AutoApproveAll=true`). WebUI provides `BlazorPermissionChannel`. Background exec uses `DenyAllPermissionChannel`.
- **`RuleEngine`** / **`PermissionService`** — Permission rules with Allow/Deny/Ask effects, pattern matching. Agent definitions carry their own `PermissionRules` and `AllowedTools`/`DeniedTools` lists.
- **Agent Modes**: `Primary` (user-facing), `SubAgent` (called by other agents), `All` (both). `AgentRuntime.Native` vs ACP-backed.

### Execution Flow

1. `ChatOrchestrator.SubmitAsync(sessionId, chatInput)` → delegates to `ExecutionJobService`
2. `ExecutionJobService` (singleton) manages concurrent executions with `ChatExecutionQueue` serializing per-session
3. `AgentLoopSchedulerHostedService` handles idle-resume and session idle timeout detection
4. `IAgentExecutor` dispatches to Native or ACP execution engine based on `AgentRuntime`
5. Events stream to subscribers via `IExecutionEventPublisher` → `SessionEventBus` → UI (SignalR/SSE)

### Configuration System

- **Agent framework config** is loaded from `~/.seeing/seeing.json` (user-level) and `./.seeing/seeing.json` (project-level) — NOT from `appsettings.json`.
- `UnifiedConfigManager` merges both sources with project-level taking precedence. Hot-reload via file watcher → `ConfigReloadService`.
- **Agent definitions** from two sources: (1) built-in C# agents registered at startup, (2) YAML frontmatter in `AGENT.md` files under `~/.seeing/agents/<name>/` and `./.seeing/agents/<name>/`. MD configs are merged on top of built-in definitions via `AgentManager.ApplyMdConfigToStoreAsync`.
- `SeeingAgentOptionsMonitor` bridges to `IOptions<SeeingAgentOptions>` / `IOptionsMonitor<SeeingAgentOptions>` for DI compatibility.
- `GatewayOptionsMonitor` handles Gateway-specific config (separate from main agent options).

### Session Management (`Seeing.Session`)

- `ISessionManager` / `SessionManager` handles create, load, save, delete with file-based persistence.
- `SessionData` contains `Messages` (list of `SessionMessage`), `SelectedAgent`, `WorkingDirectory`, `Kind` (Root/Fork/SubAgent).
- Compression: `SummarizingStrategy` (LLM-based via `ISummarizer`), `HybridStrategy`.
- Idle timeout + cleanup via `AgentLoopSchedulerHostedService`.

### Scheduler (`Seeing.Agent.Scheduler`)

- Quartz.NET-based cron job engine with SQLite persistence.
- Jobs: `AgentJob` (run an agent on schedule), `HeartbeatJob`.
- `ScheduleManager` manages job CRUD as `ITool` implementations (`CronCreateTool`, `CronListTool`, etc.).
- Schedule windows + active hours validation via `ActiveHoursChecker` and `ScheduleWindowsValidator`.
- `JsonScheduleRepository` for schedule definition storage.

### WebUI (`samples/Seeing.Agent.WebUI`)

- Blazor Server app with AntDesign 2.0 components.
- `AppState` (singleton) + `SessionState` (scoped) for UI state management.
- `BlazorPermissionChannel` handles interactive permission requests in the browser.
- `CircuitTracker` + `SeeingCircuitHandler` manage Blazor circuit lifecycle (JSDisconnectedException protection).
- `GatewayClientSupervisor` + `GatewayClientHostedService` maintain persistent gateway connections.
- Markdown rendering via Markdig + custom `MessageRendering` pipeline.

### DI Registration Pattern

Shared `ConfigSectionRegistry` — register capability modules **before** spine core; initialize **before** `Host.Start` / `app.Run`:

```csharp
var registry = new ConfigSectionRegistry();
builder.Services.AddSingleton<IConfigSectionRegistry>(registry);
builder.Services.AddSeeingModule<FileSystemModule>(registry);
builder.Services.AddSeeingModule<BasicModule>(registry);
// … other AddSeeingModule* / AddSeeing* (Acp, Scheduler, Memory, Gateway, TokenBudget)
builder.Services.AddSeeingCore(registry);
```

After `Build()`, before the host starts:
```csharp
await sp.InitializeSeeingAsync();   // load config, skills, MCP, plugins
sp.InitializeCommands();            // slash-command discovery
sp.UseTokenBudgetHooks();           // wire up hook handlers
```

### Git Integration (`src/capabilities/Seeing.Agent.Tools.Git/`)

- `IGitService` wraps git CLI operations.
- Built-in tools: `GitStatusTool`, `GitDiffTool`, `GitLogTool`, `GitCommitTool` — all implementing `ITool`.

### Key Patterns

- **Tool decorators**: `CachedToolDecorator`, `RetryToolDecorator`, `TimeoutToolDecorator` wrap any `ITool`.
- **Snapshot system**: `FileSnapshotService` takes filesystem snapshots, `DiffCalculator` computes changes.
- **Todo system**: `ITodoManager` / `TodoItem` for agent task tracking.
- **Loop detection**: `LoopDetector` identifies infinite agent loops by hashing recent messages.
- **Template engine**: `AgentTemplateEngine` + `AgentValidator` for agent generation from templates.
- **Component manager**: `IComponentManager` with `IComponentLoader` for plugin-style extension loading.
