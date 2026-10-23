## ADDED Requirements

### Requirement: Fork session command

The system SHALL provide a `/fork` command that creates a copy of the current session with a new ID using `ISessionManager.ForkAsync()`.

#### Scenario: Fork session with custom title
- **WHEN** user executes `/fork My Forked Session`
- **THEN** the system SHALL call `ISessionManager.ForkAsync()` with label "My Forked Session"
- **AND** the new session title SHALL be "My Forked Session"

#### Scenario: Fork session with default title
- **WHEN** user executes `/fork` without arguments
- **THEN** the system SHALL call `ISessionManager.ForkAsync()` without label
- **AND** the new session title SHALL be "<original title> (Fork)"

#### Scenario: Fork with no active session
- **WHEN** user executes `/fork` without an active session
- **THEN** the system SHALL return an error message "No active session"

#### Scenario: Fork returns navigation
- **WHEN** fork succeeds
- **THEN** the result SHALL include navigation to the new session `/session/{newSessionId}`