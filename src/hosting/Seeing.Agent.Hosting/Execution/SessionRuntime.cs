using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Runtime;
using Seeing.Agent.Core.Execution;

namespace Seeing.Agent.Hosting.Execution;

/// <summary>
/// 会话运行时 - 统一的「往会话放消息并激活」入口（骨架，实现见 2.3/2.4）。
/// </summary>
public sealed class SessionRuntime : ISessionRuntime, IExecutionInFlightBoundary, IDisposable, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, SessionExecutionState> _states = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionGates = new();
    private readonly ConcurrentDictionary<string, Task> _activeLoops = new();
    private readonly ConcurrentDictionary<string, byte> _stoppedSessions = new();
    private readonly ConcurrentDictionary<string, ExecutionRecord> _executions = new();
    private readonly SemaphoreSlim _globalConcurrency;
    private readonly IExecutionRunner _runner;
    private readonly IExecutionEventPublisher _publisher;
    private readonly IServiceProvider _services;
    private readonly ExecutionOptions _options;
    private readonly ILogger<SessionRuntime> _logger;

    internal SessionRuntime(
        IExecutionRunner runner,
        IExecutionEventPublisher publisher,
        IServiceProvider services,
        ExecutionOptions options,
        ILogger<SessionRuntime> logger)
    {
        _runner = runner;
        _publisher = publisher;
        _services = services;
        _options = options;
        _logger = logger;
        _globalConcurrency = new SemaphoreSlim(options.MaxConcurrentExecutions <= 0 ? int.MaxValue : options.MaxConcurrentExecutions);
    }

    public Task<InboxResult> DeliverAsync(InboxMessage message, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<bool> InterruptAsync(string sessionId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<int> StopAsync(string sessionId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<int> CancelAllInFlightAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task WaitForIdleAsync(string sessionId, CancellationToken ct = default) => throw new NotImplementedException();
    public SessionExecutionOverview GetOverview(string sessionId) => throw new NotImplementedException();
    public bool HasAnyActiveExecution() => throw new NotImplementedException();
    public IReadOnlyList<IMessageEvent> GetBufferedEvents(string sessionId) => throw new NotImplementedException();
    public IAsyncEnumerable<IMessageEvent> SubscribeEvents(string sessionId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<int> ReconcileIncompleteTasksAsync(string sessionId, CancellationToken ct = default) => throw new NotImplementedException();

    // IExecutionInFlightBoundary
    public bool HasInFlight() => throw new NotImplementedException();
    public IReadOnlyList<string> ListInFlightExecutionIds() => throw new NotImplementedException();

    public void Dispose() { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
