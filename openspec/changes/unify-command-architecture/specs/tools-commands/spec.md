## ADDED Requirements

### Requirement: MCP management command

The system SHALL provide a `/mcp` command to manage MCP servers.

#### Scenario: List MCP tools
- **WHEN** user executes `/mcp` or `/mcp list`
- **THEN** the system SHALL display a list of all MCP tools with names and descriptions

#### Scenario: No MCP tools available
- **WHEN** user executes `/mcp` and no MCP tools are configured
- **THEN** the system SHALL display "No MCP tools available"

#### Scenario: MCP subcommand not implemented
- **WHEN** user executes `/mcp add` or other subcommands
- **THEN** the system SHALL return a message indicating the feature is not yet implemented
- **AND** suggest using the Settings page

### Requirement: Tools list command

The system SHALL provide a `/tools` command to list all available tools.

#### Scenario: List all tools
- **WHEN** user executes `/tools`
- **THEN** the system SHALL display a list of all registered tools with names and descriptions
- **AND** tools SHALL be sorted alphabetically by name

#### Scenario: Tools include both built-in and MCP tools
- **WHEN** user executes `/tools`
- **THEN** the list SHALL include both built-in tools (read, write, bash, etc.) and MCP tools
