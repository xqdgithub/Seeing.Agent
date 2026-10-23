## ADDED Requirements

### Requirement: Submit execution request
The system SHALL accept execution requests via a non-blocking `Submit` method that returns immediately with an execution ID.

#### Scenario: Successful submission
- **WHEN** client calls `Submit(sessionId, input, options)`
- **THEN** system returns `ExecutionSubmitResult` with `Success=true` and a unique `ExecutionId`

#### Scenario: Execution queued
- **WHEN** client submits a request while another execution is active for the same session
- **THEN** system returns result with `Status=Queued` and `QueuePosition > 0`

### Requirement: Immediate message persistence
The system SHALL persist user messages to storage immediately upon submission, before execution begins.

#### Scenario: Message saved before execution
- **WHEN** client submits an execution request with user message
- **THEN** the user message is saved to session storage before the method returns

#### Scenario: Message survives page refresh
- **WHEN** user refreshes the page immediately after submission
- **THEN** the user message is still present when the session is reloaded

### Requirement: Sequential execution per session
The system SHALL execute only one task at a time per session, with subsequent requests queued.

#### Scenario: Single active execution
- **WHEN** an execution is running for session A
- **THEN** new requests for session A are queued with status `Queued`

#### Scenario: Queue processing
- **WHEN** the current execution completes (success, failure, or cancellation)
- **THEN** the next queued execution automatically starts

### Requirement: Execution cancellation
The system SHALL support cancellation of active or queued executions.

#### Scenario: Cancel running execution
- **WHEN** client calls `Cancel(executionId)` for a running execution
- **THEN** execution status changes to `Cancelled` and execution stops

#### Scenario: Cancel queued execution
- **WHEN** client calls `Cancel(executionId)` for a queued execution
- **THEN** execution is removed from queue with status `Cancelled`

### Requirement: Execution state query
The system SHALL provide methods to query execution state.

#### Scenario: Get session overview
- **WHEN** client calls `GetOverview(sessionId)`
- **THEN** system returns `SessionExecutionOverview` with current execution and queue length

#### Scenario: Get specific execution
- **WHEN** client calls `GetExecution(executionId)`
- **THEN** system returns `ExecutionRecord` with current status and metadata

### Requirement: Execution history
The system SHALL maintain execution history per session, limited to the most recent 100 entries.

#### Scenario: History appended on completion
- **WHEN** an execution reaches terminal state (Completed, Failed, Cancelled)
- **THEN** execution record is appended to session's execution history

#### Scenario: History limited
- **WHEN** history exceeds 100 entries
- **THEN** oldest entries are removed (FIFO)

### Requirement: Background permission handling
The system SHALL handle permission requests during background execution with configurable timeout.

#### Scenario: Permission timeout
- **WHEN** a permission request is made during background execution
- **AND** no response is received within the configured timeout (default 30 seconds)
- **THEN** permission is automatically denied

### Requirement: Resource cleanup
The system SHALL clean up resources for idle sessions.

#### Scenario: Idle session cleanup
- **WHEN** a session has no active execution and no queued tasks for the configured idle timeout (default 30 minutes)
- **THEN** the session's execution queue and event buffer are cleaned up

### Requirement: Configurable concurrency limits
The system SHALL support configurable maximum concurrent executions.

#### Scenario: Global concurrency limit
- **WHEN** `MaxConcurrentExecutions` is configured
- **THEN** system rejects new submissions when the limit is reached

#### Scenario: Unlimited concurrency
- **WHEN** `MaxConcurrentExecutions` is set to -1
- **THEN** system accepts all submissions without global limit
