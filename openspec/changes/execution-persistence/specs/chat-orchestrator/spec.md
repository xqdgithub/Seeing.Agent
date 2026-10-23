## ADDED Requirements

### Requirement: Submit execution method
`IChatOrchestrator` SHALL provide a `Submit` method for non-blocking execution submission.

#### Scenario: Submit returns immediately
- **WHEN** client calls `Submit(sessionId, input, options)`
- **THEN** method returns `ExecutionSubmitResult` without waiting for execution to complete

### Requirement: Subscribe events method
`IChatOrchestrator` SHALL provide a `SubscribeEvents` method for real-time event streaming.

#### Scenario: Subscribe returns event stream
- **WHEN** client calls `SubscribeEvents(sessionId, cancellationToken)`
- **THEN** method returns `IAsyncEnumerable<IMessageEvent>` for the specified session

### Requirement: Cancel execution method
`IChatOrchestrator` SHALL provide a `Cancel` method to stop executions.

#### Scenario: Cancel by execution ID
- **WHEN** client calls `Cancel(executionId)`
- **THEN** execution is cancelled if it exists and is not already in terminal state

### Requirement: Get overview method
`IChatOrchestrator` SHALL provide a `GetOverview` method for session execution state.

#### Scenario: Overview with active execution
- **WHEN** client calls `GetOverview(sessionId)` with an active execution
- **THEN** system returns overview with `CurrentExecution` populated

#### Scenario: Overview without active execution
- **WHEN** client calls `GetOverview(sessionId)` with no active execution
- **THEN** system returns overview with `CurrentExecution = null` and `QueueLength = 0`

### Requirement: Get buffered events method
`IChatOrchestrator` SHALL provide a `GetBufferedEvents` method for reconnection support.

#### Scenario: Retrieve recent events
- **WHEN** client calls `GetBufferedEvents(sessionId)`
- **THEN** system returns list of recent events from the buffer

## REMOVED Requirements

### Requirement: ExecuteAsync blocking method
**Reason:** Replaced by `Submit` + `SubscribeEvents` pattern for better decoupling.
**Migration:** Use `Submit(sessionId, input, options)` followed by `SubscribeEvents(sessionId, ct)` to consume events.
