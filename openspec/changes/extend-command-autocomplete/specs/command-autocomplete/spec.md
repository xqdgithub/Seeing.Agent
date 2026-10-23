## ADDED Requirements

### Requirement: Command autocomplete dropdown displays all command types

The system SHALL display a dropdown list when the user inputs `/` in the prompt text area. The dropdown SHALL include built-in commands, custom commands, tools, and skills.

#### Scenario: User triggers command autocomplete
- **WHEN** user types `/` in the prompt input
- **THEN** system displays a dropdown list containing all available commands

#### Scenario: User filters commands by search text
- **WHEN** user types `/mod` in the prompt input
- **THEN** system displays only commands whose name or description contains "mod"

### Requirement: Commands are sorted by priority

The system SHALL sort commands by priority value in ascending order. Commands with the same priority SHALL be sorted alphabetically by name.

#### Scenario: Commands displayed in priority order
- **WHEN** command dropdown is displayed
- **THEN** custom commands appear before built-in commands
- **AND** built-in commands appear before tools
- **AND** tools appear before skills

### Requirement: Command item displays relevant information

The system SHALL display for each command: name, description, icon, and category. The system MAY display keybind hint and status badges.

#### Scenario: Command item shows complete information
- **WHEN** a command is displayed in the dropdown
- **THEN** the command name is shown with an icon
- **AND** the command description is shown
- **AND** the command category is indicated

### Requirement: Selecting a command inserts it into the input

The system SHALL insert the selected command value into the prompt input when the user selects it.

#### Scenario: User selects a tool or skill command
- **WHEN** user clicks on `/bash` command
- **THEN** the input text is replaced with `/bash `

#### Scenario: User selects a built-in command
- **WHEN** user clicks on `/clear` built-in command
- **THEN** the built-in command callback is invoked
- **AND** the dropdown is closed

### Requirement: Disabled commands show visual indication

The system SHALL visually indicate when a command is disabled. The system SHALL display the reason for the disabled state.

#### Scenario: Disabled command appears grayed out
- **WHEN** a command is disabled
- **THEN** the command appears with reduced opacity
- **AND** hovering shows the disabled reason

#### Scenario: Clicking disabled command shows warning
- **WHEN** user clicks on a disabled command
- **THEN** system displays a warning message with the reason
- **AND** the command is not executed