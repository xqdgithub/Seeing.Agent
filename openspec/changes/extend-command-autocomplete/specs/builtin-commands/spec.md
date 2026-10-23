## ADDED Requirements

### Requirement: Built-in commands are predefined

The system SHALL provide the following built-in commands:

- `/help` (Basic): Display available commands list
- `/clear` (Basic): Clear current context
- `/new` (Session): Create new session
- `/model` (Agent): Switch model
- `/agent` (Agent): Switch Agent
- `/mcp` (Tools): Manage MCP servers
- `/skill` (Tools): List/switch skills
- `/init` (System): Initialize project
- `/compact` (Session): Compact context
- `/undo` (Session): Undo last message
- `/redo` (Session): Redo operation
- `/share` (Session): Share session
- `/fork` (Session): Fork session
- `/terminal` (View): Toggle terminal

#### Scenario: Built-in commands list is complete
- **WHEN** system loads built-in commands
- **THEN** all listed commands are available

### Requirement: Built-in commands have reserved names

The system SHALL reserve built-in command names. Custom commands with the same name SHALL be marked as conflicted.

#### Scenario: Custom command conflicts with built-in command
- **WHEN** a custom command is named "clear"
- **THEN** the custom command is marked as disabled
- **AND** the disabled reason indicates conflict with built-in command

### Requirement: Built-in commands trigger callbacks

The system SHALL invoke registered callbacks when a built-in command is selected, instead of inserting text.

#### Scenario: Selecting built-in command invokes handler
- **WHEN** user selects `/clear` built-in command
- **THEN** the OnBuiltInCommand callback is invoked with "clear" as parameter
- **AND** the input text is not modified

#### Scenario: Built-in command displays keybind hint
- **WHEN** user views `/new` command in dropdown
- **THEN** the command displays "Ctrl+Shift+N" keybind hint
- **AND** selecting the command invokes the "new" callback
