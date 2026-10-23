## ADDED Requirements

### Requirement: Unified execution entry point
The system SHALL provide a unified IChatOrchestrator interface for all Agent execution requests.

#### Scenario: Execute chat request
- **WHEN** a client calls `ExecuteAsync(sessionId, input, options)` with valid parameters
- **THEN** the system SHALL return an `IAsyncEnumerable<ChatEvent>` stream

#### Scenario: Session auto-creation
- **WHEN** a client calls `ExecuteAsync` with a non-existent sessionId
- **THEN** the system SHALL create a new session automatically

### Requirement: Session lifecycle management
The system SHALL manage Session lifecycle within the orchestrator.

#### Scenario: Add user message to session
- **WHEN** a chat request is received
- **THEN** the system SHALL add the user message to SessionData.Messages

#### Scenario: Add assistant message to session
- **WHEN** an Agent execution completes with a message
- **THEN** the system SHALL add the assistant message to SessionData.Messages

#### Scenario: Session persistence
- **WHEN** a chat execution completes
- **THEN** the system SHALL persist the session to storage

### Requirement: Event stream for UI updates
The system SHALL emit events for UI rendering.

#### Scenario: Session update event
- **WHEN** session data changes
- **THEN** the system SHALL emit a `ChatEvent` with type `SessionUpdated`

#### Scenario: Stream delta event
- **WHEN** the Agent produces incremental content
- **THEN** the system SHALL emit `StreamDelta` events

#### Scenario: Tool call event
- **WHEN** a tool is invoked
- **THEN** the system SHALL emit `ToolCall` events with status

### Requirement: Agent execution routing
The system SHALL route execution to appropriate Agent runtime.

#### Scenario: Native execution
- **WHEN** the Agent runtime is `Native`
- **THEN** the system SHALL execute via `NativeAgentExecutionRouter`

#### Scenario: ACP passthrough
- **WHEN** the Agent runtime is `AcpPassthrough`
- **THEN** the system SHALL execute via `AcpPassthroughExecutor`

### Requirement: Simplified input model
The system SHALL accept simplified input parameters.

#### Scenario: Text-only input
- **WHEN** `ChatInput.Text` is provided without attachments
- **THEN** the system SHALL process as text message

#### Scenario: Multimodal input
- **WHEN** `ChatInput.Attachments` includes images or files
- **THEN** the system SHALL process as multimodal message

#### Scenario: Default options
- **WHEN** `ChatOptions` is null
- **THEN** the system SHALL use default Agent and Model from configuration

### Requirement: Minimal public interface
The system SHALL NOT require external callers to provide internal implementation details.

#### Scenario: No IServiceProvider required
- **WHEN** a client calls `ExecuteAsync`
- **THEN** the system SHALL NOT require `IServiceProvider` as input parameter

#### Scenario: No AgentDefinition required
- **WHEN** a client calls `ExecuteAsync` with `AgentId` in options
- **THEN** the system SHALL resolve `AgentDefinition` internally from AgentRegistry

#### Scenario: No History required
- **WHEN** a client calls `ExecuteAsync`
- **THEN** the system SHALL load History from SessionManager internally

### Requirement: Internal context building
The system SHALL build ChatExecutionContext internally from minimal public parameters.

#### Scenario: Build context from sessionId
- **WHEN** `ExecuteAsync` is called with sessionId
- **THEN** the system SHALL build `ChatExecutionContext` with History from SessionManager

#### Scenario: Build context with Agent
- **WHEN** `ExecuteAsync` is called with AgentId in options
- **THEN** the system SHALL resolve AgentDefinition from AgentRegistry

#### Scenario: Sub-agent context creation
- **WHEN** a sub-agent is invoked internally
- **THEN** the system SHALL create child context with `ParentSessionId` and `IsTopLevel=false`
