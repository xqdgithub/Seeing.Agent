## Why

The current command system has duplicated definitions between frontend and backend, causing inconsistency and maintenance burden. Frontend defines a command list (`BuiltInCommands.cs`) that doesn't match backend implementations, and many frontend-defined commands (`/model`, `/agent`, `/mcp`, etc.) have no backend handlers. This leads to confusing UX where commands appear in autocomplete but fail with "unknown command" when executed.

Additionally, frontend's `CustomCommandLoader` duplicates backend's `CommandDiscovery` for loading Markdown commands, creating potential sync issues.

## What Changes

- **Remove** `samples/Seeing.Agent.WebUI/Commands/BuiltInCommands.cs` (frontend hardcoded command list)
- **Remove** `samples/Seeing.Agent.WebUI/Commands/CustomCommandLoader.cs` (frontend command loader)
- **Remove** `/terminal` command from frontend (use UI button for terminal toggle instead)
- **Add** `src/Seeing.Agent.App/Commands/SessionCommands.cs` - `/fork` only (undo/redo/share deferred)
- **Add** `src/Seeing.Agent.App/Commands/AgentCommands.cs` - `/agent`, `/model` (immediate switch)
- **Add** `src/Seeing.Agent.App/Commands/ToolsCommands.cs` - `/mcp`, `/tools`
- **Add** `samples/Seeing.Agent.WebUI/Services/CommandListService.cs` - frontend service to fetch commands from backend
- **Modify** `Session.razor` - inject `ICommandRegistry`, fetch command list from backend
- **Modify** `PromptInput.razor` - remove frontend command class references, accept commands as parameter
- **Modify** `ChatOrchestratorServiceExtensions.cs` - register new command providers

## Capabilities

### New Capabilities

- `command-list-service`: Frontend service that fetches available commands from backend `ICommandRegistry`, providing single source of truth for command autocomplete and UI display.
- `session-commands`: Backend commands for session management (`/fork` only; `/undo`, `/redo`, `/share` deferred).
- `agent-commands`: Backend commands for agent/model switching (`/agent`, `/model`). Commands execute immediately and affect next conversation.
- `tools-commands`: Backend commands for tool management (`/mcp`, `/tools`).

### Modified Capabilities

None - this is a new unified architecture, not modifying existing spec-level behavior.

## Impact

**Files Deleted:**
- `samples/Seeing.Agent.WebUI/Commands/BuiltInCommands.cs`
- `samples/Seeing.Agent.WebUI/Commands/CustomCommandLoader.cs`

**Files Added:**
- `src/Seeing.Agent.App/Commands/SessionCommands.cs`
- `src/Seeing.Agent.App/Commands/AgentCommands.cs`
- `src/Seeing.Agent.App/Commands/ToolsCommands.cs`
- `samples/Seeing.Agent.WebUI/Services/CommandListService.cs`

**Files Modified:**
- `src/Seeing.Agent.App/ChatOrchestratorServiceExtensions.cs`
- `samples/Seeing.Agent.WebUI/Pages/Session.razor`
- `samples/Seeing.Agent.WebUI/Components/PromptInput.razor`
- `samples/Seeing.Agent.WebUI/Models/QuickCommandViewModel.cs`
- `samples/Seeing.Agent.WebUI/Services/CommandAvailabilityChecker.cs` (migrate logic to CommandListService)

**API Changes:**
- `PromptInput.razor`: New parameter `AvailableCommands` replaces `AvailableCustomCommands`
- Frontend no longer defines commands; all commands come from `ICommandRegistry`
- `/terminal` removed - use UI button for terminal toggle

**Dependencies:**
- `ICommandRegistry` injected into Blazor components via DI
- `CommandMetadata` mapped to `CommandItemViewModel` for frontend display
