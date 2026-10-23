## ADDED Requirements

### Requirement: Event publication
The system SHALL publish execution events to subscribers via a `Publish` method.

#### Scenario: Publish stream event
- **WHEN** an execution produces an `IMessageEvent` (e.g., `StreamDeltaEvent`, `StreamCompleteEvent`)
- **THEN** the event is published to all subscribers of that session

### Requirement: Event subscription
The system SHALL allow clients to subscribe to execution events via an async stream.

#### Scenario: Subscribe to session events
- **WHEN** client calls `SubscribeEvents(sessionId, cancellationToken)`
- **THEN** system returns `IAsyncEnumerable<IMessageEvent>` that yields events as they occur

#### Scenario: Subscription ends on cancellation
- **WHEN** the cancellation token is triggered
- **THEN** the async enumerable completes gracefully

### Requirement: Event buffering
The system SHALL buffer the most recent N events per session for reconnection recovery.

#### Scenario: Retrieve buffered events
- **WHEN** client calls `GetBufferedEvents(sessionId)`
- **THEN** system returns the most recent events (up to configured buffer size, default 100)

#### Scenario: Buffer overflow
- **WHEN** buffer exceeds configured size
- **THEN** oldest events are discarded (circular buffer behavior)

### Requirement: Buffer cleanup
The system SHALL clear event buffer when session execution reaches terminal state.

#### Scenario: Buffer cleared on completion
- **WHEN** execution reaches terminal state (Completed, Failed, Cancelled)
- **THEN** the event buffer for that session is cleared

### Requirement: Multiple subscribers
The system SHALL support multiple concurrent subscribers per session.

#### Scenario: Multiple UI tabs
- **WHEN** two clients subscribe to the same session
- **THEN** both receive the same events
