## Why

When users refresh the page during an AI agent execution in the Blazor Server application, the conversation content is lost. The root cause is twofold: (1) user messages are only saved to disk after the agent completes execution, and (2) the execution lifecycle is tightly coupled to the UI's SignalR connection. This creates a poor user experience where refreshing the browser window during a long-running AI task results in lost work.

This change is needed now because the application will support multiple client types (Blazor UI, CLI, WebSocket Gateway) and the current architecture cannot support decoupled execution.

## What Changes

- **New background execution service**: Execution tasks run independently of UI connections in a hosted background service
- **Immediate message persistence**: User messages are saved to disk immediately upon submission, before execution begins
- **Queued execution model**: Multiple requests to the same session are queued and processed sequentially
- **Event streaming architecture**: UI clients subscribe to execution events via an async stream, rather than owning the execution lifecycle
- **BREAKING**: `IChatOrchestrator.ExecuteAsync()` is replaced with `Submit()` + `SubscribeEvents()` pattern
- **BREAKING**: `MainLayout.razor` no longer auto-loads the most recent session on initialization

## Capabilities

### New Capabilities

- `execution-engine`: Background execution service with queuing, event streaming, and lifecycle management independent of UI connections
- `event-publisher`: Pub/sub event system for broadcasting execution events to multiple subscribers

### Modified Capabilities

- `chat-orchestrator`: Interface changes from blocking execution to submit/subscribe pattern

## Impact

**Core Components:**
- `src/Seeing.Agent.App/Execution/` - New module with execution engine components
- `src/Seeing.Agent.App/ChatOrchestrator.cs` - Interface and implementation changes
- `src/Seeing.Agent.App/IChatOrchestrator.cs` - New method signatures

**UI Components:**
- `samples/Seeing.Agent.WebUI/Pages/Session.razor` - Refactor to use submit/subscribe pattern
- `samples/Seeing.Agent.WebUI/Shared/MainLayout.razor` - Remove auto-load logic
- `samples/Seeing.Agent.WebUI/State/SessionState.cs` - Add execution state properties
- `samples/Seeing.Agent.WebUI/Program.cs` - Register new services

**Affected Clients:**
- Blazor Server UI
- CLI (future)
- WebSocket Gateway (future)
