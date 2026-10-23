## 1. Backend Command Providers

- [x] 1.1 Create `src/Seeing.Agent.App/Commands/SessionCommands.cs`
  - `/fork`: Use `ISessionManager.ForkAsync()` with optional label argument
  - **Deferred**: `/undo`, `/redo`, `/share` (requires additional design)
- [x] 1.2 Create `src/Seeing.Agent.App/Commands/AgentCommands.cs`
  - `/agent`: List agents or switch (immediate effect)
  - `/model`: List models or switch (immediate effect)
  - Inject `IAgentRegistry` and `ILlmService`
- [x] 1.3 Create `src/Seeing.Agent.App/Commands/ToolsCommands.cs`
  - `/mcp`: List MCP tools (filter by `mcp_` prefix)
  - `/tools`: List all registered tools
  - Inject `ToolInvoker` and use `GetToolSchemasAsync()`
- [x] 1.4 Register new command providers in `ChatOrchestratorServiceExtensions.cs`
- [x] 1.5 Verify commands are discovered and registered via `ICommandRegistry`

## 2. Frontend Command Service

- [x] 2.1 Add `Aliases` property to `CommandItemViewModel` in `QuickCommandViewModel.cs`
- [x] 2.2 Create `CommandListService.cs` in WebUI Services folder
- [x] 2.3 Implement `GetAvailableCommands()` method mapping `CommandMetadata` to `CommandItemViewModel`
- [x] 2.4 Implement icon mapping by `CommandCategory`:
  - Basic → Home, Navigation → Compass, Agent → Robot
  - Tools → Tool, System → Setting, Extension → Appstore, Other → Code
- [x] 2.5 Implement keybind hint mapping for common commands
- [x] 2.6 Implement `CanExecute()` logic for session-required commands (`/clear`, `/fork`, `/compact`)
- [x] 2.7 Register `CommandListService` in DI container (Scoped)

## 3. Frontend Component Updates

- [ ] 3.1 Update `Session.razor` to inject `ICommandRegistry` and `CommandListService`
- [ ] 3.2 Replace `_customCommands` with `_availableCommands` from backend
- [ ] 3.3 Remove `CustomCommandLoader` field and `LoadCustomCommandsAsync()` call
- [ ] 3.4 Update `PromptInput.razor` to accept `AvailableCommands` parameter
- [ ] 3.5 Remove reference to frontend `BuiltInCommands` class in `LoadCommands()`
- [ ] 3.6 Pass commands from `Session.razor` to `PromptInput` component

## 4. Remove Duplicate Frontend Files

- [ ] 4.1 Delete `samples/Seeing.Agent.WebUI/Commands/BuiltInCommands.cs`
- [ ] 4.2 Delete `samples/Seeing.Agent.WebUI/Commands/CustomCommandLoader.cs`
- [ ] 4.3 Remove `@using Seeing.Agent.WebUI.Commands` from affected razor files
- [ ] 4.4 Migrate `CommandAvailabilityChecker` logic to `CommandListService.CanExecute()`

## 5. Handle Removed Commands

- [ ] 5.1 Remove `/terminal` from command list - use UI button for terminal toggle instead
- [ ] 5.2 Ensure `/undo`, `/redo`, `/share` do NOT appear in autocomplete (not implemented)
- [ ] 5.3 Ensure `/init` does NOT appear in autocomplete (deferred)

## 6. Testing and Verification

- [ ] 6.1 Verify all new commands appear in autocomplete dropdown (`/agent`, `/model`, `/mcp`, `/tools`, `/fork`)
- [ ] 6.2 Test `/fork` command creates new session with copied messages
- [ ] 6.3 Test `/agent` and `/model` commands list and switch (immediate effect)
- [ ] 6.4 Test `/mcp` and `/tools` commands list available tools
- [ ] 6.5 Verify commands not in backend do NOT appear in autocomplete (`/terminal`, `/undo`, `/init`)
- [ ] 6.6 Verify existing commands (`/help`, `/clear`, `/new`, `/exit`, `/skills`) still work
- [ ] 6.7 Verify session-required commands are disabled when no active session