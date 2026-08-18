# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Test

```bash
# Build the entire solution (uses CPM, no restore needed for first build)
dotnet build

# Run all tests
dotnet test

# Run a specific test project
dotnet test tests/Seeing.Agent.Tests

# Run a single test (filter by fully-qualified name)
dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~ClassName.TestMethod"

# Run the Blazor WebUI (primary dev app; also starts Gateway server on :8765)
dotnet run --project samples/Seeing.Agent.WebUI

# Run the headless Gateway Server (agent + gateway without UI)
dotnet run --project samples/Seeing.Gateway.Server
```

Target framework: **net10.0**. Package versions are managed centrally via `Directory.Packages.props` (CPM).

## Architecture

### Solution Layering

```
Seeing.Agent (core lib)       ← IAgent, ITool, ISkill, IHookHandler, RuleEngine, MCP, Snapshot, LLM
  ↑
Seeing.Agent.App (orchestra)  ← ChatOrchestrator, ExecutionJobService, command system
  ↑
Seeing.Agent.WebUI (Blazor)   ← the main sample application
```

**Supporting libraries** (all target `net10.0`, reference `Seeing.Agent` as needed):

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

### Core Concepts (All in `src/Seeing.Agent/`)

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

The main extension methods chain:
```csharp
builder.Services.AddSeeingAgent(configuration);   // core
builder.Services.AddSeeingAcp();                   // ACP
builder.Services.AddSeeingScheduler();             // Quartz
builder.Services.AddTokenBudgetIntegration();      // token tracking
builder.Services.AddMemoryServices();              // memory/vector
builder.Services.AddSeeingGatewayServer();         // gateway
builder.Services.AddChatOrchestrator();            // execution engine
builder.Services.AddExecutionEngine();             // background runner
```

After building the service provider, call:
```csharp
sp.InitializeSeeingAgentAsync();   // skills, MCP, plugins
sp.InitializeCommands();           // slash-command discovery
sp.UseTokenBudgetHooks();          // wire up hook handlers
```

### Git Integration (`src/Seeing.Agent/Git/`)

- `IGitService` wraps git CLI operations.
- Built-in tools: `GitStatusTool`, `GitDiffTool`, `GitLogTool`, `GitCommitTool` — all implementing `ITool`.

### Key Patterns

- **Tool decorators**: `CachedToolDecorator`, `RetryToolDecorator`, `TimeoutToolDecorator` wrap any `ITool`.
- **Snapshot system**: `FileSnapshotService` takes filesystem snapshots, `DiffCalculator` computes changes.
- **Todo system**: `ITodoManager` / `TodoItem` for agent task tracking.
- **Loop detection**: `LoopDetector` identifies infinite agent loops by hashing recent messages.
- **Template engine**: `AgentTemplateEngine` + `AgentValidator` for agent generation from templates.
- **Component manager**: `IComponentManager` with `IComponentLoader` for plugin-style extension loading.
