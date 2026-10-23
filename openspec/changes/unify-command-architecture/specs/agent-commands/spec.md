## ADDED Requirements

### Requirement: Agent switch command

The system SHALL provide a `/agent` command to switch or list available agents. Switching takes effect immediately for the next conversation.

#### Scenario: List available agents
- **WHEN** user executes `/agent` without arguments
- **THEN** the system SHALL display a list of all available agents with names and descriptions

#### Scenario: Switch to valid agent
- **WHEN** user executes `/agent coder` where "coder" is a valid agent name
- **THEN** the system SHALL return a success message "Switched to agent: coder"
- **AND** include metadata "agentSwitch" with value "coder"
- **AND** the switch SHALL take effect immediately for the next conversation

#### Scenario: Switch to invalid agent
- **WHEN** user executes `/agent nonexistent`
- **THEN** the system SHALL return an error message "Agent not found: nonexistent"

### Requirement: Model switch command

The system SHALL provide a `/model` command to switch or list available models. Switching takes effect immediately for the next conversation.

#### Scenario: List available models
- **WHEN** user executes `/model` without arguments
- **THEN** the system SHALL display a list of all available models with names

#### Scenario: Switch to valid model
- **WHEN** user executes `/model gpt-4` where "gpt-4" is a valid model name
- **THEN** the system SHALL return a success message "Switched to model: gpt-4"
- **AND** include metadata "modelSwitch" with value "gpt-4"
- **AND** the switch SHALL take effect immediately for the next conversation

#### Scenario: Switch to invalid model
- **WHEN** user executes `/model nonexistent`
- **THEN** the system SHALL return an error message "Model not found: nonexistent"
