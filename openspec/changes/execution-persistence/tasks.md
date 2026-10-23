## 1. Core Models

- [x] 1.1 Create `ExecutionStatus.cs` enum with values: Pending, Queued, Running, Completed, Failed, Cancelled
- [x] 1.2 Create `ExecutionRecord.cs` model with ExecutionId, SessionId, Status, Input, Options, timestamps, queue position
- [x] 1.3 Create `ExecutionSubmitResult.cs` with Success, ExecutionId, Status, QueuePosition, Message, Error
- [x] 1.4 Create `SessionExecutionOverview.cs` with CurrentExecution, QueueLength, QueuedExecutions
- [x] 1.5 Create `ExecutionOptions.cs` configuration class with MaxConcurrentExecutions, EventBufferSize, ExecutionHistoryLimit, PermissionTimeout, SessionIdleTimeout, CleanupInterval
- [x] 1.6 Create `CircularBuffer.cs` generic class for event buffering with configurable size

## 2. Event Publisher

- [x] 2.1 Create `IExecutionEventPublisher.cs` interface with Publish and SubscribeAsync methods
- [x] 2.2 Create `ExecutionEventPublisher.cs` implementation using Channel<T> per session
- [x] 2.3 Implement circular buffer integration for event history
- [x] 2.4 Add support for multiple subscribers per session

## 3. Execution Queue

- [x] 3.1 Create `SessionExecutionQueue.cs` internal class with SemaphoreSlim-based locking
- [x] 3.2 Implement SubmitAsync method with queue position tracking
- [x] 3.3 Implement CompleteAsync method to transition execution and start next
- [x] 3.4 Implement CancelAsync method for running and queued executions
- [x] 3.5 Add cancellation token source management per execution

## 4. Execution Job Service

- [x] 4.1 Create `ExecutionJobService.cs` as Singleton service
- [x] 4.2 Implement Submit method with immediate message persistence (using IServiceProvider.CreateScope)
- [x] 4.3 Implement background execution processing loop
- [x] 4.4 Implement Cancel method delegating to session queue
- [x] 4.5 Implement GetOverview, GetExecution, GetBufferedEvents query methods
- [x] 4.6 Implement SubscribeEvents using ExecutionEventPublisher
- [x] 4.7 Add timer-based cleanup for idle session queues
- [x] 4.8 Implement execution history appending with size limit

## 5. Background Permission Channel

- [x] 5.1 Create `BackgroundPermissionChannel.cs` implementing IPermissionChannel
- [x] 5.2 Implement timeout-based auto-reject for permission requests
- [x] 5.3 Add configurable timeout from ExecutionOptions

## 6. Chat Orchestrator Interface Changes

- [x] 6.1 Add Submit, Cancel, GetOverview, GetExecution, GetBufferedEvents, SubscribeEvents to `IChatOrchestrator.cs`
- [x] 6.2 Mark ExecuteAsync as [Obsolete] with migration guidance
- [x] 6.3 Update `ChatOrchestrator.cs` to delegate to ExecutionJobService
- [x] 6.4 Create DI scope for each execution in ChatOrchestrator

## 7. Service Registration

- [x] 7.1 Add AddExecutionEngine extension method in ServiceCollectionExtensions.cs
- [x] 7.2 Register ExecutionOptions with configuration binding
- [x] 7.3 Register ExecutionJobService as Singleton
- [x] 7.4 Register IExecutionEventPublisher as Singleton
- [x] 7.5 Update Program.cs to call AddExecutionEngine

## 8. UI Layer Changes

- [x] 8.1 Add ExecutionStatus and QueuePosition properties to SessionState.cs
- [ ] 8.2 Refactor Session.razor OnSubmitMessage to use Submit + SubscribeEvents
- [ ] 8.3 Add queue badge UI component in Session.razor
- [ ] 8.4 Implement execution state recovery on page load in Session.razor
- [ ] 8.5 Update cancel button to use Cancel(executionId) in Session.razor
- [ ] 8.6 Remove auto-load logic from MainLayout.razor OnInitializedAsync

## 9. Testing

- [ ] 9.1 Create unit tests for SessionExecutionQueue concurrent access
- [ ] 9.2 Create unit tests for ExecutionJobService Submit/Cancel flow
- [ ] 9.3 Create unit tests for event buffering and reconnection
- [ ] 9.4 Create integration test for page refresh scenario
- [ ] 9.5 Create integration test for queued execution processing
- [ ] 9.6 Create integration test for permission timeout handling
