## ADDED Requirements

### Requirement: Command availability is checked dynamically

The system SHALL check command availability based on current session state when displaying the command dropdown.

#### Scenario: Availability checked on dropdown display
- **WHEN** user opens the command dropdown
- **THEN** system checks availability for each command
- **AND** unavailable commands are marked as disabled

### Requirement: Undo command requires message history

The `/undo` command SHALL be disabled when there are no user messages in the session.

#### Scenario: Undo enabled with message history
- **WHEN** session has at least one user message
- **THEN** `/undo` command is enabled

#### Scenario: Undo disabled without message history
- **WHEN** session has no user messages
- **THEN** `/undo` command is disabled
- **AND** disabled reason is "No messages to undo"

### Requirement: Redo command requires revert state

The `/redo` command SHALL be disabled when there is no revert point in the session.

#### Scenario: Redo enabled with revert state
- **WHEN** session has a revert point from previous undo
- **THEN** `/redo` command is enabled

#### Scenario: Redo disabled without revert state
- **WHEN** session has no revert point
- **THEN** `/redo` command is disabled
- **AND** disabled reason is "No operation to redo"

### Requirement: Clear command requires messages

The `/clear` command SHALL be disabled when the session is empty.

#### Scenario: Clear enabled with messages
- **WHEN** session has messages
- **THEN** `/clear` command is enabled

#### Scenario: Clear disabled on empty session
- **WHEN** session has no messages
- **THEN** `/clear` command is disabled
- **AND** disabled reason is "Session is empty"

### Requirement: Compact command requires messages

The `/compact` command SHALL be disabled when the session is empty.

#### Scenario: Compact enabled with messages
- **WHEN** session has messages
- **THEN** `/compact` command is enabled

#### Scenario: Compact disabled on empty session
- **WHEN** session has no messages
- **THEN** `/compact` command is disabled
- **AND** disabled reason is "Session is empty"

### Requirement: Share and fork commands require active session

The `/share` and `/fork` commands SHALL be disabled when there is no active session.

#### Scenario: Share enabled with active session
- **WHEN** there is an active session
- **THEN** `/share` command is enabled

#### Scenario: Share disabled without session
- **WHEN** there is no active session
- **THEN** `/share` command is disabled
- **AND** disabled reason is "No active session"