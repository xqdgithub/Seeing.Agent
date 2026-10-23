## Context

The current architecture tightly couples AI agent execution to the Blazor Server UI lifecycle:

1. **Message persistence timing**: User messages are added to an in-memory `SessionData` object, but only persisted to disk (`SaveAsync`) after the agent completes execution. If the user refreshes during execution, the message is lost.

2. **Execution lifecycle coupling**: The `SessionState` service is Scoped (per SignalR connection), and execution is driven via `await foreach` from `Session.razor`. When the SignalR connection drops, execution is cancelled.

3. **Multiple client requirement**: The system needs to support Blazor UI, CLI, and WebSocket Gateway clients with a unified execution model.

**Key Constraints:**
- Blazor Server uses SignalR with Scoped services per connection
- `IChatOrchestrator` currently depends on `IPermissionChannel` (Scoped)
- Session data is persisted via `FileSessionStore` to `~/.seeing/sessions/*.json`

## Goals / Non-Goals

**Goals:**
- Decouple execution lifecycle from UI connections
- Persist user messages immediately upon submission
- Support queued execution (one active execution per session)
- Provide event streaming for UI updates
- Support multiple client types with the same API
- Implement timeout-based permission handling for background execution

**Non-Goals:**
- Distributed execution across multiple server instances
- Persistent execution state for server restart recovery
- Complex multi-tenant isolation
- Real-time multi-tab collaboration (only one tab controls at a time)

## Decisions

### 1. Background Service vs. Task Queue

**Decision:** Use `ExecutionJobService` as a Singleton service with internal task processing (not `IHostedService`).

**Rationale:**
- `IHostedService` lifecycle management adds complexity for DI scope handling
- Singleton service with `IServiceProvider.CreateScope()` per execution is simpler and more testable
- Avoids the complexity of background service orchestration

**Alternatives Considered:**
- `IHostedService`: Requires complex DI scope management; overkill for single-instance scenario
- Distributed task queue (e.g., Hangfire, MassTransit): Adds infrastructure dependency; not needed for single-server deployment

### 2. Event Streaming Architecture

**Decision:** Use `Channel<IMessageEvent>` per session for event broadcasting.

**Rationale:**
- `Channel<T>` is built-in, efficient, and supports multiple readers
- Circular buffer for last N events enables reconnection recovery
- No external messaging infrastructure required

**Alternatives Considered:**
- `IObservable<T>` pattern: Requires Rx extensions; more complex for async streams
- SignalR groups: Tied to SignalR; doesn't work for CLI clients

### 3. Permission Handling in Background Execution

**Decision:** Implement `BackgroundPermissionChannel` with configurable timeout (default 30 seconds).

**Rationale:**
- Background execution cannot wait indefinitely for user interaction
- Timeout with auto-reject is predictable and safe
- Configurable timeout allows tuning for different use cases

**Alternatives Considered:**
- Auto-approve: Security risk; could allow unintended operations
- Queue permissions for later: Overly complex; user may never reconnect

### 4. Queue Model Per Session

**Decision:** One `SessionExecutionQueue` per session with `SemaphoreSlim` for concurrency control.

**Rationale:**
- Ensures sequential execution per session (avoid race conditions)
- Uses async-friendly locking (`SemaphoreSlim` not `lock`)
- Simple model: one active, rest queued

**Alternatives Considered:**
- Global queue with session affinity: More complex; same result
- Concurrent execution per session: Risk of message ordering issues

### 5. Message Persistence Timing

**Decision:** Save user message immediately after `Submit()`, before execution starts.

**Rationale:**
- Guarantees message persistence regardless of execution outcome
- Simple mental model: submit = saved
- Enables recovery if execution fails

**Alternatives Considered:**
- Save on first event: Complex; could still lose messages
- Save on completion only: Current broken behavior

## Risks / Trade-offs

| Risk | Impact | Mitigation |
|------|--------|------------|
| Server restart loses in-progress execution | User sees incomplete conversation | User message persisted; can re-submit |
| Memory leak from unclosed channels | Degraded performance over time | Timer-based cleanup of idle session queues |
| Permission timeout during legitimate long operation | Operation auto-rejected | Configurable timeout; document expected durations |
| DI scope issues with Singleton service | Runtime errors | Use `CreateScope()` per execution; extensive testing |
| Event buffer overflow | Lost events for reconnection | Circular buffer with configurable size (default 100) |

## Migration Plan

1. **Phase 1**: Add new execution engine alongside existing `ExecuteAsync`
2. **Phase 2**: Migrate `Session.razor` to new `Submit/Subscribe` pattern
3. **Phase 3**: Remove deprecated `ExecuteAsync` method
4. **Phase 4**: Update CLI/Gateway clients to new API

**Rollback Strategy:**
- Each phase is independently deployable
- Old `ExecuteAsync` can be restored if issues arise
- Feature flag to toggle between old/new execution path (optional)

## Open Questions

- None currently. All design decisions have been clarified through user discussion.
