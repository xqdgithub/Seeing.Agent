## ADDED Requirements

### Requirement: Custom commands are loaded from directories

The system SHALL load custom commands from Markdown files in the following directories:
- User-level: `~/.seeing/commands/*.md`
- Project-level: `./.seeing/commands/*.md`

#### Scenario: Load user-level custom commands
- **WHEN** system loads custom commands
- **THEN** commands from `~/.seeing/commands/*.md` are included

#### Scenario: Load project-level custom commands
- **WHEN** system loads custom commands with a workspace root
- **THEN** commands from `./.seeing/commands/*.md` are included

### Requirement: Project-level commands override user-level commands

The system SHALL prioritize project-level custom commands over user-level commands when they have the same name.

#### Scenario: Same name at different levels
- **WHEN** both user-level and project-level have a command named "review"
- **THEN** the project-level command is used
- **AND** the user-level command is not shown

### Requirement: Custom commands are parsed from Markdown

The system SHALL parse Markdown files with YAML frontmatter. The frontmatter SHALL contain: `name` (required), `description` (optional), `category` (optional).

#### Scenario: Parse valid Markdown command
- **WHEN** a Markdown file contains valid frontmatter with name field
- **THEN** a command item is created with the parsed metadata

#### Scenario: Skip invalid Markdown command
- **WHEN** a Markdown file lacks required name field
- **THEN** the file is skipped without error

### Requirement: Custom commands have higher priority than built-in commands

The system SHALL assign custom commands lower priority values than built-in commands for sorting purposes.

#### Scenario: Custom commands appear first in dropdown
- **WHEN** command dropdown is displayed
- **THEN** custom commands appear before built-in commands in the list

### Requirement: Custom commands conflicting with built-in commands are disabled

The system SHALL mark custom commands as disabled when their name conflicts with a built-in command name.

#### Scenario: Custom command name conflicts with built-in
- **WHEN** a custom command is named "help"
- **THEN** the custom command is marked as disabled
- **AND** the disabled reason is "Conflicts with built-in command /help"