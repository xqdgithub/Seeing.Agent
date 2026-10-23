## ADDED Requirements

### Requirement: Command type classification
The system SHALL classify commands by execution behavior using CommandType enum.

#### Scenario: System command
- **WHEN** a command has `CommandType.System`
- **THEN** the system SHALL execute the command and return without Agent execution

#### Scenario: Skill command
- **WHEN** a command has `CommandType.Skill`
- **THEN** the system SHALL expand the command, update Session, and continue Agent execution

#### Scenario: Default command
- **WHEN** a command has `CommandType.Default`
- **THEN** the system SHALL pass through to Agent for handling

### Requirement: System command execution
The system SHALL execute system commands and return results via events.

#### Scenario: New session command
- **WHEN** user inputs `/new`
- **THEN** the system SHALL create a new session and emit `NavigateEvent` with the new session URL

#### Scenario: Clear session command
- **WHEN** user inputs `/clear`
- **THEN** the system SHALL clear current session messages and emit `CommandResultEvent`

#### Scenario: Help command
- **WHEN** user inputs `/help`
- **THEN** the system SHALL emit `CommandResultEvent` with help text

#### Scenario: Compact command
- **WHEN** user inputs `/compact`
- **THEN** the system SHALL compress session context and emit `CommandResultEvent` with result

### Requirement: Skill command expansion
The system SHALL expand Skill commands into LLM context.

#### Scenario: Skill content expansion
- **WHEN** user inputs `/<skill-name> <arguments>`
- **THEN** the system SHALL replace the message content with expanded Skill template

#### Scenario: Skill template format
- **WHEN** a Skill command is expanded
- **THEN** the system SHALL use `<skill-invoke>` XML format with skill name, description, content, and user request

#### Scenario: Skill not found
- **WHEN** a Skill command references a non-existent skill
- **THEN** the system SHALL emit `CommandResultEvent` with error message

### Requirement: Command result events
The system SHALL emit appropriate events for command execution results.

#### Scenario: Success event
- **WHEN** a command executes successfully
- **THEN** the system SHALL emit `CommandResultEvent` with `Success=true`

#### Scenario: Failure event
- **WHEN** a command execution fails
- **THEN** the system SHALL emit `CommandResultEvent` with `Success=false` and error message

#### Scenario: Navigation event
- **WHEN** a command requires navigation (e.g., `/new`)
- **THEN** the system SHALL emit `NavigateEvent` with target URL

### Requirement: Dynamic skill registration
The system SHALL support dynamic registration of Skill commands.

#### Scenario: Skill discovery
- **WHEN** the application starts
- **THEN** the system SHALL discover all SKILL.md files and register as commands

#### Scenario: Skill command metadata
- **WHEN** a Skill is registered
- **THEN** the command SHALL have `Type=CommandType.Skill` and `Category=CommandCategory.Tools`
