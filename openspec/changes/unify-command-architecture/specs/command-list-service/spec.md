## ADDED Requirements

### Requirement: Command list service provides available commands

The system SHALL provide a frontend service that retrieves all available commands from the backend `ICommandRegistry`.

#### Scenario: Fetch commands on component initialization
- **WHEN** a Blazor component calls `CommandListService.GetAvailableCommands()`
- **THEN** the service SHALL return a list of `CommandItemViewModel` objects derived from `ICommandRegistry.GetAllMetadata()`

#### Scenario: Commands include metadata for display
- **WHEN** commands are fetched from backend
- **THEN** each command SHALL include: name, description, category, aliases, and icon mapping

#### Scenario: Hidden commands are excluded
- **WHEN** commands are fetched from backend
- **THEN** commands with `IsHidden = true` SHALL NOT be included in the result

### Requirement: Command icons mapped by category

The system SHALL map command categories to appropriate AntDesign icons for frontend display.

#### Scenario: Basic commands use home icon
- **WHEN** command category is `CommandCategory.Basic`
- **THEN** the icon SHALL be `IconType.Outline.Home`

#### Scenario: Agent commands use robot icon
- **WHEN** command category is `CommandCategory.Agent`
- **THEN** the icon SHALL be `IconType.Outline.Robot`

#### Scenario: Tools commands use tool icon
- **WHEN** command category is `CommandCategory.Tools`
- **THEN** the icon SHALL be `IconType.Outline.Tool`

### Requirement: Command keybind hints preserved

The system SHALL allow keybind hints to be associated with commands for display in the command palette.

#### Scenario: Keybind hint for new session command
- **WHEN** command name is "new"
- **THEN** the keybind hint SHALL be "Ctrl+Shift+N"

#### Scenario: No keybind for commands without shortcuts
- **WHEN** command has no defined shortcut
- **THEN** the keybind field SHALL be null or empty
