using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Core.Llm;
using Seeing.Agent.Llm;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Prompts;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Core.Permission;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Core.Events;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Models;
using Seeing.Agent.Core.Commands;
using Seeing.Agent.Core.Compression;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Core.Instructions;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Core.Scheduling;
using Seeing.Agent.Core.Modules;
using Seeing.Agent.Core.Scenarios;
using Seeing.Agent.Core.Services;
using Seeing.Agent.Core.Execution;
using Seeing.Session.Core;
using Seeing.Session.Management;

namespace Seeing.Agent.Hosting.Execution;

/// <summary>
/// Background execution service that manages execution jobs independently of UI connections.
/// Supports queuing per session, event streaming, and automatic cleanup.
/// </summary>
public class ExecutionJobService : IDisposable, IAsyncDisposable, IExecutionStatusProvider, IExecutionSubmitter, IExecutionInFlightBoundary
{
    private readonly ConcurrentDictionary<string, SessionExecutionQueue> _sessionQueues = new();
    private readonly ConcurrentDictionary<string, ExecutionRecord> _executions = new();
    private readonly ConcurrentDictionary<string, CircularBuffer<IMessageEvent>> _eventBuffers = new();
    private readonly IServiceProvider _serviceProvider;
    private readonly IExecutionEventPublisher _eventPublisher;
    private readonly ExecutionOptions _options;
    private readonly IOptionsMonitor<SeeingAgentOptions> _seeingAgentOptions;
    private readonly IConfigSectionStore _configStore;
    private readonly CompactionRunner _compactionRunner;
    private readonly ILogger<ExecutionJobService> _logger;
    private readonly Timer _cleanupTimer;
    private readonly IAgentLoopScheduler? _loopScheduler;
    private readonly IScenarioCatalog? _scenarioCatalog;
    // 正在执行的执行体集合（StartAsync 成功后加入，finally 移除）。供 DisposeAsync 有界等待，
    // 避免在途执行体仍持有取消令牌时释放其 CTS。
    private readonly ConcurrentDictionary<string, byte> _inFlightExecutions = new();
    private bool _disposed;

    /// <summary>
    /// Creates a new ExecutionJobService.
    /// </summary>
    public ExecutionJobService(
        IServiceProvider serviceProvider,
        IExecutionEventPublisher eventPublisher,
        ExecutionOptions options,
        IOptionsMonitor<SeeingAgentOptions> seeingAgentOptions,
        IConfigSectionStore configStore,
        ILogger<ExecutionJobService> logger,
        CompactionRunner compactionRunner,
        IAgentLoopScheduler? loopScheduler = null,
        IScenarioCatalog? scenarioCatalog = null)
    {
        _serviceProvider = serviceProvider;
        _eventPublisher = eventPublisher;
        _options = options ?? new ExecutionOptions();
        _seeingAgentOptions = seeingAgentOptions;
        _configStore = configStore;
        _compactionRunner = compactionRunner ?? throw new ArgumentNullException(nameof(compactionRunner));
        _logger = logger;
        _loopScheduler = loopScheduler;
        _scenarioCatalog = scenarioCatalog;

        // Setup cleanup timer
        _cleanupTimer = new Timer(
            CleanupIdleSessions,
            null,
            _options.CleanupInterval,
            _options.CleanupInterval);

        _logger.LogInformation("ExecutionJobService initialized with options: MaxConcurrent={MaxConcurrent}, EventBuffer={EventBuffer}",
            _options.MaxConcurrentExecutions, _options.EventBufferSize);
    }

    /// <summary>
    /// Submits a new execution request.
    /// User messages are saved immediately before execution begins.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="input">The user input.</param>
    /// <param name="options">Execution options (agent, model, etc.).</param>
    /// <returns>The submission result with execution ID and status.</returns>
    public async Task<ExecutionSubmitResult> SubmitAsync(
        string sessionId,
        ChatInput input,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrEmpty(sessionId))
            return ExecutionSubmitResult.Failed("Session ID is required");

        // Check global concurrency limit
        if (_options.MaxConcurrentExecutions > 0)
        {
            var activeCount = _sessionQueues.Values.Count(q => q.HasActiveExecution);
            if (activeCount >= _options.MaxConcurrentExecutions)
                return ExecutionSubmitResult.Failed("Maximum concurrent executions reached. Please try again later.");
        }

        // Generate execution ID
        var executionId = $"exec_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}"[..24];
        var now = DateTime.UtcNow;

        // Create execution record
        var record = new ExecutionRecord
        {
            ExecutionId = executionId,
            SessionId = sessionId,
            Input = input,
            Options = options,
            Status = ExecutionStatus.Pending,
            CreatedAt = now
        };

        // Get or create session queue
        var queue = _sessionQueues.GetOrAdd(sessionId, _ => new SessionExecutionQueue());

        // Check queue size limit
        if (queue.QueueLength >= _options.MaxQueueSizePerSession)
            return ExecutionSubmitResult.Failed($"Queue is full (max {_options.MaxQueueSizePerSession} items). Please wait for current executions to complete.");

        // ⭐ Immediately update and save session state before execution starts
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var sessionManager = scope.ServiceProvider.GetRequiredService<ISessionManager>();
            var modelManager = scope.ServiceProvider.GetRequiredService<IModelManager>();
            var instructionManager = scope.ServiceProvider.GetRequiredService<IInstructionManager>();
            var workspaceProvider = scope.ServiceProvider.GetRequiredService<IWorkspaceProvider>();
            var executionWorld = scope.ServiceProvider.GetRequiredService<IExecutionWorld>();
            var session = await sessionManager.EnsureSessionAsync(
                sessionId,
                scenario: scope.ServiceProvider.GetService<IDefaultWorkModeProvider>()?.GetDefaultScenario());
            TryBackfillSessionOutbound(session, options?.ChannelId, options?.UserId);

            // ⭐ Persist model/mode selection to session (ensures they're saved even if execution fails)
            ApplyInboundModelAndMode(session, options?.ModelId, options?.ModeId, modelManager);
            if (options?.ThinkingEffort is not null)
            {
                session.SelectedThinkingEffort = options.ThinkingEffort.Trim();
                session.UpdatedAt = DateTime.Now;
            }

            var cwd = options?.WorkingDirectory
                ?? session.WorkingDirectory
                ?? executionWorld.Cwd;
            if (!string.Equals(session.WorkingDirectory, cwd, StringComparison.Ordinal))
            {
                session.WorkingDirectory = cwd;
            }

            if (options?.SkipInstructionInject != true)
            {
                try
                {
                    await instructionManager.InjectIfNeededAsync(
                        session,
                        cwd,
                        workspaceProvider.GetProjectRoot(),
                        CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Failed to inject project instructions for execution {ExecutionId}",
                        executionId);
                }
            }

            if (options?.SkipUserMessagePersist != true)
            {
                var userMessage = BuildUserMessage(input);
                session.AddMessage(userMessage);
            }

            await sessionManager.SaveAsync(sessionId);

            if (options?.SkipUserMessagePersist != true)
            {
                _logger.LogDebug("User message saved immediately for execution {ExecutionId}", executionId);
            }
        }
        catch (Exception ex) when (options?.SkipUserMessagePersist == true)
        {
            _logger.LogWarning(ex, "Failed to save model/mode selection for execution {ExecutionId}", executionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save user message for execution {ExecutionId}", executionId);
            return ExecutionSubmitResult.Failed($"Failed to save message: {ex.Message}");
        }

        // Submit to queue。
        // 竞态防护：本方法在获取队列后还需异步保存会话，期间 CleanupIdleSessions 可能已回收并
        // Dispose 该队列实例（GetOrAdd 返回的引用失效）。此时重新获取新队列并重试入队，
        // 避免已 Dispose 队列的 ObjectDisposedException 外溢。
        while (true)
        {
            try
            {
                await queue.SubmitAsync(record);
                break;
            }
            catch (ObjectDisposedException)
            {
                if (_disposed)
                    throw;
                queue = _sessionQueues.GetOrAdd(sessionId, _ => new SessionExecutionQueue());
            }
        }

        _executions[executionId] = record;

        // Start processing if not already running（传入实际入队的队列引用，避免清理/替换竞态下按会话重查错队列）
        _ = ProcessQueueAsync(queue);

        var result = record.Status == ExecutionStatus.Queued
            ? ExecutionSubmitResult.Queued(executionId, record.QueuePosition)
            : ExecutionSubmitResult.Succeeded(executionId);

        _logger.LogInformation("Execution {ExecutionId} submitted with status {Status}", executionId, record.Status);

        return result;
    }

    /// <inheritdoc />
    public async Task<bool> CancelAsync(string executionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_executions.TryGetValue(executionId, out var record))
            return false;

        if (record.IsTerminal)
            return false;

        if (!_sessionQueues.TryGetValue(record.SessionId, out var queue))
            return false;

        var outcome = await queue.CancelAsync(executionId).ConfigureAwait(false);
        if (outcome == ExecutionCancelOutcome.NotFound)
            return false;

        _logger.LogInformation("Execution {ExecutionId} cancelled", executionId);

        // 终态事件发布方唯一：已启动（Running）项由执行体 finally 统一发布；
        // 从未启动项（排队项 / 尚未 StartAsync 的当前项）由这里发布。
        // 判定与状态迁移同一队列锁内完成（outcome），消除取消/启动窗口的重复终态事件。
        if (outcome == ExecutionCancelOutcome.CancelledNotStarted)
            PublishCancelledComplete(record.SessionId, executionId);

        // 不终止会话事件流：后续排队项仍需向订阅者发布输出。
        // 队列取消已推进：启动下一项执行（StartAsync 校验防止双开）
        var next = queue.CurrentExecution;
        if (next != null && next.Status == ExecutionStatus.Pending)
        {
            _ = ProcessExecutionAsync(next, queue);
        }

        return true;
    }

    /// <summary>
    /// 取消会话级联取消：取消指定会话及其所有子会话（Relation==Child）下未终态的执行。
    /// 参考 <see cref="CancelAsync(string, CancellationToken)"/> 的队列推进逻辑：取消当前项后提升下一排队项，
    /// 循环直至无活跃/排队执行，因此本方法会清空该会话的执行队列。
    /// </summary>
    /// <param name="sessionId">父会话或子会话 ID</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>已取消的执行数量</returns>
    public virtual async Task<int> CancelBySessionAsync(string sessionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return 0;

        var sessionIds = new List<string> { sessionId };

        // 收集所有子会话（通过 _serviceProvider 解析 ISessionGroupManager 查询）
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var groupManager = scope.ServiceProvider.GetRequiredService<ISessionGroupManager>();
            var children = await groupManager.ListChildrenAsync(sessionId, ct);
            foreach (var child in children)
                sessionIds.Add(child.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "取消会话执行时枚举子会话失败，仅取消会话自身: {SessionId}", sessionId);
        }

        var count = 0;
        foreach (var id in sessionIds)
        {
            if (!_sessionQueues.TryGetValue(id, out var queue))
                continue;

            // 循环取消当前项（CancelAsync 会推进队列），直至无活跃执行
            while (true)
            {
                var current = queue.CurrentExecution;
                if (current == null || current.IsTerminal)
                    break;

                var outcome = await queue.CancelAsync(current.ExecutionId);
                if (outcome == ExecutionCancelOutcome.NotFound)
                    break;

                count++;
                // 从未启动项无人发布终态，这里补齐（已启动项由执行体 finally 发布）
                if (outcome == ExecutionCancelOutcome.CancelledNotStarted)
                    PublishCancelledComplete(id, current.ExecutionId);
            }

            // 队列中残余的排队项（理论为 0，防御性兜底）
            foreach (var queued in queue.GetQueuedExecutions())
            {
                if (queued.IsTerminal)
                    continue;

                var outcome = await queue.CancelAsync(queued.ExecutionId);
                if (outcome == ExecutionCancelOutcome.NotFound)
                    continue;

                count++;
                if (outcome == ExecutionCancelOutcome.CancelledNotStarted)
                    PublishCancelledComplete(id, queued.ExecutionId);
            }
        }

        if (count > 0)
            _logger.LogInformation("已取消会话相关执行: SessionId={SessionId}, Count={Count}", sessionId, count);

        return count;
    }

    /// <summary>
    /// Gets the execution overview for a session.
    /// </summary>
    public SessionExecutionOverview GetOverview(string sessionId)
    {
        if (!_sessionQueues.TryGetValue(sessionId, out var queue))
        {
            return new SessionExecutionOverview();
        }

        return new SessionExecutionOverview
        {
            CurrentExecution = queue.CurrentExecution,
            QueueLength = queue.QueueLength,
            QueuedExecutions = queue.GetQueuedExecutions()
        };
    }

    /// <summary>
    /// Gets an execution record by ID.
    /// </summary>
    public ExecutionRecord? GetExecution(string executionId)
    {
        return _executions.TryGetValue(executionId, out var record) ? record : null;
    }

    /// <inheritdoc />
    public bool HasAnyActiveExecution()
    {
        foreach (var queue in _sessionQueues.Values)
        {
            if (queue.HasActiveExecution || queue.HasQueued)
                return true;
        }

        return false;
    }

    /// <inheritdoc />
    public bool HasInFlight() => HasAnyActiveExecution();

    /// <inheritdoc />
    public IReadOnlyList<string> ListInFlightExecutionIds()
    {
        var ids = new List<string>();
        foreach (var queue in _sessionQueues.Values)
        {
            var current = queue.CurrentExecution;
            if (current is { IsTerminal: false })
                ids.Add(current.ExecutionId);

            foreach (var queued in queue.GetQueuedExecutions())
            {
                if (!queued.IsTerminal)
                    ids.Add(queued.ExecutionId);
            }
        }

        return ids;
    }

    /// <inheritdoc />
    public async Task<int> CancelAllInFlightAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var count = 0;
        foreach (var id in ListInFlightExecutionIds())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await CancelAsync(id, cancellationToken).ConfigureAwait(false))
                count++;
        }

        return count;
    }

    /// <summary>
    /// Gets buffered events for reconnection.
    /// </summary>
    public IReadOnlyList<IMessageEvent> GetBufferedEvents(string sessionId)
    {
        return _eventPublisher.GetBufferedEvents(sessionId);
    }

    /// <summary>
    /// Subscribes to execution events for a session.
    /// </summary>
    public IAsyncEnumerable<IMessageEvent> SubscribeEvents(string sessionId, CancellationToken cancellationToken)
    {
        return _eventPublisher.SubscribeAsync(sessionId, cancellationToken);
    }

    /// <summary>
    /// 等待指定执行进入终态（供 idle resume 使用）。
    /// </summary>
    public async Task WaitForExecutionAsync(string executionId, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_executions.TryGetValue(executionId, out var record) || record.IsTerminal)
                return;
            await Task.Delay(50, cancellationToken);
        }
    }

    /// <summary>
    /// Cleans up idle session queues.
    /// </summary>
    private void CleanupIdleSessions(object? state)
    {
        var now = DateTime.UtcNow;
        var sessionsToRemove = new List<string>();

        // Take a snapshot to avoid collection modified exception
        var snapshot = _sessionQueues.ToArray();

        foreach (var (sessionId, queue) in snapshot)
        {
            // 锁内快照判定（消除 _pendingQueue.Count / LastActiveTime 的数据竞争）
            var state0 = queue.GetSnapshot();
            if (state0.HasActiveExecution || state0.HasQueued)
                continue;

            // Check idle timeout
            if (now - state0.LastActiveTime > _options.SessionIdleTimeout)
            {
                sessionsToRemove.Add(sessionId);
            }
        }

        foreach (var sessionId in sessionsToRemove)
        {
            if (!_sessionQueues.TryRemove(sessionId, out var queue))
                continue;

            // 原子判定并退休：仅真正空闲的队列可回收；移除瞬间被并发 Submit 变活跃则拒绝（返回 false）。
            // 该判定在同一队列锁内完成，避免「快照空闲 → Dispose」之间被塞入执行而丢项。
            if (!queue.TryRetireIfIdle())
            {
                // 放回字典成功：交由后续清理周期处理，避免误弃在途/排队执行导致其永不推进。
                if (_sessionQueues.TryAdd(sessionId, queue))
                    continue;

                // 放回失败：并发 SubmitAsync 已对已删 key GetOrAdd 出新队列，字典不再指向旧队列。
                // 旧队列可能仍持有在途/排队项：若直接丢弃则执行永不推进、CTS 泄漏（同会话双开）。
                // 因此先取消旧队列全部未终态执行（终态唯一发布），再释放。
                _logger.LogWarning(
                    "空闲清理检测到并发替换，取消孤儿队列未终态执行: {SessionId}",
                    sessionId);
                _ = CancelStaleQueueAsync(sessionId, queue);
                continue;
            }

            _eventPublisher.CompleteSession(sessionId);
            _logger.LogDebug("Cleaned up idle session queue: {SessionId}", sessionId);
        }
    }

    /// <summary>
    /// 取消已从字典摘除的孤儿队列中的全部未终态执行，随后释放队列。
    /// 从未启动项由本方法发布终态事件；已启动项由各自执行体 finally 发布。
    /// </summary>
    private async Task CancelStaleQueueAsync(string sessionId, SessionExecutionQueue queue)
    {
        try
        {
            while (true)
            {
                var state = queue.GetSnapshot();
                if (!state.HasActiveExecution && !state.HasQueued)
                    break;

                var current = queue.CurrentExecution;
                if (current is { IsTerminal: false })
                {
                    var outcome = await queue.CancelAsync(current.ExecutionId).ConfigureAwait(false);
                    if (outcome == ExecutionCancelOutcome.NotFound)
                        break;
                    if (outcome == ExecutionCancelOutcome.CancelledNotStarted)
                        PublishCancelledComplete(sessionId, current.ExecutionId);
                    continue;
                }

                var queued = queue.GetQueuedExecutions().FirstOrDefault(item => !item.IsTerminal);
                if (queued is null)
                    break;

                var queuedOutcome = await queue.CancelAsync(queued.ExecutionId).ConfigureAwait(false);
                if (queuedOutcome == ExecutionCancelOutcome.NotFound)
                    break;
                if (queuedOutcome == ExecutionCancelOutcome.CancelledNotStarted)
                    PublishCancelledComplete(sessionId, queued.ExecutionId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "取消孤儿队列执行失败: {SessionId}", sessionId);
        }
        finally
        {
            queue.Dispose();
        }
    }

    /// <summary>
    /// 发布「已取消」终态事件（供从未启动项的唯一发布方使用）。
    /// </summary>
    private void PublishCancelledComplete(string sessionId, string executionId)
    {
        _eventPublisher.Publish(sessionId, new ExecutionCompleteEvent
        {
            SessionId = sessionId,
            ExecutionId = executionId,
            Status = ExecutionStatus.Cancelled
        });
    }

    /// <summary>
    /// Cleans up an execution record after a delay.
    /// </summary>
    private async Task CleanupExecutionAsync(string executionId)
    {
        await Task.Delay(TimeSpan.FromMinutes(5));

        _executions.TryRemove(executionId, out _);
    }

    /// <summary>
    /// 释放资源（<see cref="IDisposable"/> 契约，同步快速路径）。
    /// <para>
    /// 只做「快速释放」：取消在途/排队执行（不等待执行体结束）、释放清理定时器并回收队列与记录。
    /// 「有界等待在途执行体走完 finally」的语义完全交给 <see cref="DisposeAsync"/>；宿主支持时应优先
    /// <c>await DisposeAsync()</c>。此路径不做 <c>GetAwaiter().GetResult()</c> 同步包装，
    /// 避免阻塞最多 5s，或带 SynchronizationContext 时的死锁风险。
    /// </para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _cleanupTimer.Dispose();

        // 与 DisposeAsync 不同，此处无需 CancelAsync 逐项推进队列：SessionExecutionQueue.Dispose
        // 会同步取消当前项，并在不释放在途 CTS 的前提下回收排队项（在途 CTS 交由执行体 finally 释放）。
        ReleaseQueuesAndRecords();

        _logger.LogInformation("ExecutionJobService disposed (sync fast path)");
    }

    /// <summary>
    /// 异步释放全部资源：取消所有在途执行，有界等待在途执行体走完 finally 后再回收队列与记录。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        _cleanupTimer.Dispose();

        // Take a snapshot to avoid collection modified exception
        var snapshot = _sessionQueues.ToArray();
        foreach (var (_, queue) in snapshot)
        {
            // 停止清理：取消全部未终态执行（替代 BackgroundTaskManager.StopAsync → CancelAll）
            try
            {
                while (true)
                {
                    var current = queue.CurrentExecution;
                    if (current == null || current.IsTerminal)
                        break;
                    await queue.CancelAsync(current.ExecutionId).ConfigureAwait(false);
                }
            }
            catch
            {
                // ignore
            }
        }

        // 有界等待在途执行体走完 finally（释放各自 CTS），避免随后过早释放令牌引发 ObjectDisposedException。
        try
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (_inFlightExecutions.Count > 0 && DateTime.UtcNow < deadline)
                await Task.Delay(20).ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }

        ReleaseQueuesAndRecords();

        _logger.LogInformation("ExecutionJobService disposed");
    }

    /// <summary>
    /// 回收全部会话队列并清空执行记录；仅释放「从未启动」记录的 CTS。
    /// <para>
    /// 已启动记录的 CTS 由执行体 finally → <see cref="SessionExecutionQueue.CompleteAsync"/> 释放，
    /// 此处不得提前释放，否则在途执行体仍持有令牌时会触发 ObjectDisposedException。
    /// </para>
    /// </summary>
    private void ReleaseQueuesAndRecords()
    {
        foreach (var (_, queue) in _sessionQueues.ToArray())
            queue.Dispose();

        _sessionQueues.Clear();

        foreach (var record in _executions.Values)
        {
            if (record.IsTerminal && record.StartedAt == default)
            {
                record.Cts?.Dispose();
                record.Cts = null;
            }
        }
        _executions.Clear();
    }
}
