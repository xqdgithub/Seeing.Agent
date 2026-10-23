## 1. Model Extension

- [x] 1.1 Extend CommandItemViewModel in QuickCommandViewModel.cs with new fields: IsBuiltIn, IsCustom, Category, Priority, Keybind, IsDisabled, DisabledReason, Source

## 2. Built-in Commands Implementation

- [x] 2.1 Create BuiltInCommands.cs in Commands/ folder with static command definitions
- [x] 2.2 Define all 14 built-in commands: help, clear, new, model, agent, mcp, skill, init, compact, undo, redo, share, fork, terminal
- [x] 2.3 Add Names HashSet for conflict detection
- [x] 2.4 Assign appropriate categories (Basic, Session, Agent, Tools, System, View)
- [x] 2.5 Add keybind hints for commands (e.g., Ctrl+Shift+N for /new)

## 3. Custom Command Loader Implementation

- [x] 3.1 Create CustomCommandLoader.cs in Commands/ folder
- [x] 3.2 Implement LoadAsync method with workspaceRoot parameter
- [x] 3.3 Load user-level commands from ~/.seeing/commands/*.md
- [x] 3.4 Load project-level commands from ./.seeing/commands/*.md
- [x] 3.5 Parse Markdown files using existing YAML frontmatter format
- [x] 3.6 Mark commands conflicting with built-in names as disabled with reason

## 4. Command Availability Checker Implementation

- [x] 4.1 Create CommandAvailabilityChecker.cs in Services/ folder
- [x] 4.2 Implement CanExecute method with commandName and SessionState parameters
- [x] 4.3 Implement GetDisabledReason method with commandName and SessionState parameters
- [x] 4.4 Add availability rule: /undo requires user messages
- [x] 4.5 Add availability rule: /redo requires revert state
- [x] 4.6 Add availability rule: /clear requires messages
- [x] 4.7 Add availability rule: /compact requires messages
- [x] 4.8 Add availability rule: /share and /fork require active session

## 5. PromptInput Component Updates

- [x] 5.1 Add AvailableCustomCommands parameter to PromptInput
- [x] 5.2 Add OnBuiltInCommand EventCallback parameter
- [x] 5.3 Inject CommandAvailabilityChecker service
- [x] 5.4 Update LoadCommands to add custom commands with conflict detection
- [x] 5.5 Update LoadCommands to add built-in commands with availability check
- [x] 5.6 Update LoadCommands to sort by Priority then Name
- [x] 5.7 Update SelectCommand to handle disabled commands
- [x] 5.8 Update SelectCommand to invoke callback for built-in commands
- [x] 5.9 Update dropdown UI to show category badge
- [x] 5.10 Update dropdown UI to show keybind hint
- [x] 5.11 Update dropdown UI to show disabled state styling

## 6. Session Page Updates

- [x] 6.1 Add CustomCommandLoader instance and _customCommands field
- [x] 6.2 Call LoadCustomCommandsAsync in OnInitializedAsync
- [x] 6.3 Pass _customCommands to PromptInput component
- [x] 6.4 Implement HandleBuiltInCommand callback
- [x] 6.5 Add handler for clear command
- [x] 6.6 Add handler for new command
- [x] 6.7 Add handler for model command
- [x] 6.8 Add handler for agent command
- [x] 6.9 Add handler for help command

## 7. Styling Updates

- [x] 7.1 Add CSS for disabled command items
- [x] 7.2 Add CSS for category badges
- [x] 7.3 Add CSS for keybind hints
- [x] 7.4 Add CSS for custom/built-in indicator badges

## 8. Testing and Verification

- [ ] 8.1 Verify built-in commands appear in dropdown
- [ ] 8.2 Verify custom commands load from both directories
- [ ] 8.3 Verify project-level overrides user-level
- [ ] 8.4 Verify conflict detection marks custom commands as disabled
- [ ] 8.5 Verify availability checker disables commands appropriately
- [ ] 8.6 Verify sorting order
- [ ] 8.7 Verify built-in command callbacks are invoked
- [ ] 8.8 Verify tool/skill commands still insert text
