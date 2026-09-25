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

        // Start processing if not already running
        _ = ProcessQueueAsync(sessionId);

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

        var cancelled = await queue.CancelAsync(executionId).ConfigureAwait(false);

        if (cancelled)
        {
            _logger.LogInformation("Execution {ExecutionId} cancelled", executionId);

            // 执行体已启动（Running）时，完成事件由执行体 finally 统一发布，避免重复。
            // 从未启动的项（排队项 / 尚未启动的当前项）由这里发布。
            if (record.StartedAt == default)
            {
                _eventPublisher.Publish(record.SessionId, new ExecutionCompleteEvent
                {
                    SessionId = record.SessionId,
                    ExecutionId = executionId,
                    Status = ExecutionStatus.Cancelled
                });
            }

            // 不终止会话事件流：后续排队项仍需向订阅者发布输出。
            // 队列取消已推进：启动下一项执行（StartAsync 校验防止双开）
            var next = queue.CurrentExecution;
            if (next != null && next.Status == ExecutionStatus.Pending)
            {
                _ = ProcessExecutionAsync(next);
            }
        }

        return cancelled;
    }

    /// <summary>
    /// Cancels an execution.
    /// </summary>
    /// <param name="executionId">The execution ID to cancel.</param>
    /// <returns>True if cancelled, false if not found or already terminal.</returns>
    /// 同步兼容入口：内部委托 <see cref="CancelAsync"/>；有交互上下文的调用方应优先使用异步入口。
    public bool Cancel(string executionId)
        => CancelAsync(executionId).ConfigureAwait(false).GetAwaiter().GetResult();

    /// <summary>
    /// 取消会话级联取消：取消指定会话及其所有子会话（Relation==Child）下未终态的执行。
    /// 参考 <see cref="Cancel(string)"/> 的队列推进逻辑：取消当前项后提升下一排队项，
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
                if (await queue.CancelAsync(current.ExecutionId))
                    count++;
            }

            // 队列中残余的排队项（理论为 0，防御性兜底）
            foreach (var queued in queue.GetQueuedExecutions())
            {
                if (queued.IsTerminal)
                    continue;
                if (await queue.CancelAsync(queued.ExecutionId))
                    count++;
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
    /// Processes the queue for a session.
    /// </summary>
    private async Task ProcessQueueAsync(string sessionId)
    {
        if (!_sessionQueues.TryGetValue(sessionId, out var queue))
            return;

        // 只启动当前 Pending 项；后续排队项由 ProcessExecutionAsync.finally → CompleteAsync 链式启动。
        // 禁止 while 循环续跑，否则会与 finally 中的 ProcessExecutionAsync(next) 双开同一条执行。
        var current = queue.CurrentExecution;
        if (current == null)
            return;

        if (current.Status != ExecutionStatus.Pending)
            return;

        await ProcessExecutionAsync(current);
    }

    /// <summary>
    /// Processes a single execution.
    /// </summary>
    private async Task ProcessExecutionAsync(ExecutionRecord record)
    {
        // Create scope for this execution
        using var scope = _serviceProvider.CreateScope();
        var sessionManager = scope.ServiceProvider.GetRequiredService<ISessionManager>();
        var agentRegistry = scope.ServiceProvider.GetRequiredService<IAgentRegistry>();
        var executionRouter = scope.ServiceProvider.GetRequiredService<IAgentExecutor>();
        var agentSelectionResolver = scope.ServiceProvider.GetRequiredService<AgentSelectionResolver>();
        var modelManager = scope.ServiceProvider.GetRequiredService<IModelManager>();
        var workspaceProvider = scope.ServiceProvider.GetRequiredService<IWorkspaceProvider>();
        var executionWorld = scope.ServiceProvider.GetRequiredService<IExecutionWorld>();
        var commandRegistry = scope.ServiceProvider.GetRequiredService<ICommandRegistry>();

        var queue = _sessionQueues[record.SessionId];

        // Mark as running（若已被取消/推进则返回 false，避免把已终态记录重新置 Running）
        if (!await queue.StartAsync())
        {
            // 记录已终态（如启动窗口期被取消）：不会再有 finally → CompleteAsync 释放其 CTS，此处兜底释放。
            // 若返回 false 仅因记录已 Running（非终态），则不得释放，避免破坏在途执行持有的令牌。
            if (record.IsTerminal)
            {
                record.Cts?.Dispose();
                record.Cts = null;
            }
            return;
        }
        record.StartedAt = DateTime.UtcNow;

        // 快照本记录专属取消令牌并全程使用：令牌绑定执行记录，取消后队列推进更换 current 不影响本执行，
        // 消除“换靶”导致的 exec 串扰（闪断根因）。CTS 已由 Submit/StartAsync 保证非空。
        var executionToken = record.Cts?.Token ?? CancellationToken.None;
        _loopScheduler?.SetLoopBusy(record.SessionId, true);

        _logger.LogInformation("Execution {ExecutionId} started", record.ExecutionId);

        // Publish execution started event
        _eventPublisher.Publish(record.SessionId, new ExecutionStartedEvent
        {
            SessionId = record.SessionId,
            ExecutionId = record.ExecutionId
        });

        try
        {
            var session = await sessionManager.EnsureSessionAsync(
                record.SessionId,
                scenario: scope.ServiceProvider.GetService<IDefaultWorkModeProvider>()?.GetDefaultScenario());

            // 自动压缩门控：TokenBudget 标记 + 配置开启时，每轮 Agent 循环开始前触发压缩
            // GetSection 契约应返回非 null；防御性 ?. 避免测试/错误 mock 返回 null 时整轮执行 NRE 短路
            var autoCompaction = _configStore.GetSection<TokenBudgetAutoCompactionPeek>("TokenBudget")
                ?.AutoCompactionEnabled == true;
            if (autoCompaction &&
                session.PendingCompaction &&
                session.Messages.Count > 0)
            {
                // 统一入口：Started 先于摘要 Delta 发布，UI 进度时序正确
                var compactionOutcome = await _compactionRunner.RunAsync(session.Id, reason: "auto");
                // 失败防抖：不保留标记，避免每轮循环反复触发耗时的压缩尝试拖慢对话
                if (!compactionOutcome.Success)
                {
                    _logger.LogWarning("自动压缩失败，跳过本轮重试: {SessionId} {Error}",
                        session.Id, compactionOutcome.ErrorMessage);
                }
                session.PendingCompaction = false;
            }

            // Build execution context with background permission channel
            // 会话级结算 + schema 在此快照；本轮中途改 session.Scenario 不影响已算结果
            var context = await BuildExecutionContextAsync(
                session, record, agentRegistry, agentSelectionResolver, modelManager, workspaceProvider, executionWorld,
                scope.ServiceProvider);

            // 旁路生成标题（不阻塞主对话；命令不生成标题）
            if (record.Options?.SkipUserMessagePersist != true && !IsCommandInput(record.Input?.Text))
            {
                var titleSource = ResolveTitleSourceText(record.Input);
                if (!string.IsNullOrWhiteSpace(titleSource))
                {
                    var titleEnsuring = scope.ServiceProvider.GetRequiredService<ISessionTitleService>();
                    var fallbackModel = context.RequestModelId ?? session.SelectedModel;
                    _ = EnsureTitleFireAndForgetAsync(
                        titleEnsuring,
                        record.SessionId,
                        titleSource,
                        fallbackModel);
                }
            }

            // Process command if applicable
            var inputText = record.Input?.Text;
            // 命令要求结束本轮（shouldContinue=false / shouldExit）时短路，不再执行 Agent：
            // 避免把命令文本作为普通消息发送给大模型（如 /compact 后仍发起 LLM 请求导致失败）
            var commandConsumedExecution = false;
            if (IsCommandInput(inputText))
            {
                CommandResultEvent? lastCommandEvent = null;
                await foreach (var cmdEvent in ProcessCommandAsync(
                    record.SessionId, inputText, session, context, commandRegistry, executionToken))
                {
                    if (cmdEvent != null)
                    {
                        _eventPublisher.Publish(record.SessionId, cmdEvent);
                        // 短路判定仅看命令结果事件（ProcessCommandAsync 只 yield 命令结果或 null）
                        if (cmdEvent is CommandResultEvent commandResult)
                        {
                            lastCommandEvent = commandResult;
                        }
                    }
                }
                commandConsumedExecution = lastCommandEvent != null && !lastCommandEvent.ShouldContinue;
            }

            if (!commandConsumedExecution)
            {
                // Build history
                var messages = BuildHistoryFromSession(session);

                // 服务端负责将事件投影到 Session 并落盘；UI 只订阅展示
                var eventTracker = new ChatEventTracker();

                // schema 快照事件：与随后 ChatRequest.Tools 同源（context.ToolSchemas）
                var schemaSnapshot = new SchemaSnapshotEvent
                {
                    SessionId = record.SessionId,
                    ExecutionId = record.ExecutionId,
                    ToolIds = context.ToolSchemas?
                        .Where(s => s.Function != null)
                        .Select(s => s.Function.Name)
                        .ToArray()
                        ?? Array.Empty<string>(),
                    SectionIds = context.SectionIds
                };
                eventTracker.ApplyEvent(session, schemaSnapshot);
                _eventPublisher.Publish(record.SessionId, schemaSnapshot);
                if (ShouldPersistEvent(schemaSnapshot))
                    await sessionManager.SaveAsync(record.SessionId);

                // Execute agent
                // 取消不再主动 throw：执行器（事件流水线）会产出终态事件（工具 Cancelled / LoopCancelledEvent），
                // 这些事件必须在取消后仍被处理与发布。OCE 由下方 catch 兜底（执行器未转换为事件的极端情况）。
                // 令牌已在本方法开头绑定本记录并快照（executionToken），BuildAgentContext 与 ExecuteAsync 共用同一令牌。
                var loopFailed = false;
                string? loopError = null;
                await foreach (var evt in executionRouter.ExecuteAsync(
                    context.Agent,
                    messages,
                    BuildAgentContext(context, executionToken),
                    executionToken))
                {
                    // 事件流语义：LLM/Agent 失败以 ErrorEvent + LoopCompleteEvent(Success=false) 表达（不抛异常）
                    // 若不在此捕获，下方正常结束路径会把 record 标记为 Completed，导致 task_status 无法判定失败。
                    if (evt is ErrorEvent err)
                    {
                        loopFailed = true;
                        loopError = err.Message;
                    }
                    else if (evt is LoopCompleteEvent loopEnd && !loopEnd.Success)
                    {
                        loopFailed = true;
                        loopError ??= loopEnd.Error;
                    }

                    var liveSession = sessionManager.Get(record.SessionId) ?? session;
                    eventTracker.ApplyEvent(liveSession, evt);

                    // 先投影再发布，保证 UI 读到的是已写入的 SessionData
                    _eventPublisher.Publish(record.SessionId, evt);

                    if (ShouldPersistEvent(evt))
                        await sessionManager.SaveAsync(record.SessionId);
                }

                if (loopFailed)
                {
                    record.Status = ExecutionStatus.Failed;
                    record.ErrorMessage = loopError;
                }
            }

            // 执行器以终态事件正常结束时：若本执行已被取消（CancelAsync 已置 Cancelled 并推进队列）或
            // 队列已推进（取消竞态下 status 尚未同步），保留取消态；否则标记完成。
            // 事件流已标记 Failed（ErrorEvent / LoopCompleteEvent(Success=false)）的保留失败态。
            if (record.Status != ExecutionStatus.Cancelled &&
                record.Status != ExecutionStatus.Failed &&
                queue.CurrentExecution?.ExecutionId != record.ExecutionId)
                record.Status = ExecutionStatus.Cancelled;
            else if (record.Status != ExecutionStatus.Cancelled &&
                     record.Status != ExecutionStatus.Failed)
                record.Status = ExecutionStatus.Completed;

            // 事件流失败（非异常）或 catch 异常失败：统一标记会话 Error 状态。
            // 执行失败后 CurrentExecution 会在 finally 中清空，仅靠执行记录无法长期判定失败，
            // task_status 回落判定依赖 SessionStatus。
            if (record.Status == ExecutionStatus.Failed)
                MarkSessionError(sessionManager, record.SessionId, record.ErrorMessage);
            else if (record.Status == ExecutionStatus.Completed)
                ClearSessionError(sessionManager, record.SessionId);

            _logger.LogInformation("Execution {ExecutionId} completed with status {Status}", record.ExecutionId, record.Status);
        }
        catch (OperationCanceledException)
        {
            record.Status = ExecutionStatus.Cancelled;
            _logger.LogInformation("Execution {ExecutionId} was cancelled", record.ExecutionId);

            try
            {
                var liveSession = sessionManager.Get(record.SessionId);
                if (IncompleteToolCallMarker.MarkCancelled(liveSession, "用户取消") > 0)
                    await sessionManager.SaveAsync(record.SessionId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "取消后标记未完成 Task 失败: {ExecutionId}", record.ExecutionId);
            }
        }
        catch (Exception ex)
        {
            record.Status = ExecutionStatus.Failed;
            record.ErrorMessage = ex.Message;
            _logger.LogError(ex, "Execution {ExecutionId} failed", record.ExecutionId);

            // 标记会话 Error 状态：task_status 回落判定依赖 SessionStatus
            MarkSessionError(sessionManager, record.SessionId, record.ErrorMessage);

            // 失败路径兜底：标记未走到终态的工具调用（pending/running）为 cancelled，
            // 避免异常退出后遗留孤儿的"运行中"工具状态（对齐 OperationCanceledException 路径）。
            try
            {
                var liveSession = sessionManager.Get(record.SessionId);
                if (IncompleteToolCallMarker.MarkCancelled(liveSession, $"执行失败：{ex.Message}") > 0)
                    await sessionManager.SaveAsync(record.SessionId);
            }
            catch (Exception markerEx)
            {
                _logger.LogWarning(markerEx, "失败后标记未完成工具调用异常: {ExecutionId}", record.ExecutionId);
            }

            // Publish error event
            _eventPublisher.Publish(record.SessionId, new ErrorEvent
            {
                SessionId = record.SessionId,
                Message = ex.Message
            });
        }
        finally
        {
            record.CompletedAt = DateTime.UtcNow;
            _loopScheduler?.SetLoopBusy(record.SessionId, false);

            // Final save（先写 history 再落盘，避免 history 永远落后一次）
            try
            {
                await AppendExecutionHistoryAsync(sessionManager, record);
                await sessionManager.SaveAsync(record.SessionId);
                // 执行终态落盘：flush 确保最终状态持久化
                await sessionManager.FlushAsync(record.SessionId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save final state for execution {ExecutionId}", record.ExecutionId);
            }

            // Publish completion event
            _eventPublisher.Publish(record.SessionId, new ExecutionCompleteEvent
            {
                SessionId = record.SessionId,
                ExecutionId = record.ExecutionId,
                Status = record.Status
            });

            // Complete the execution and start next
            var nextExecution = await queue.CompleteAsync(record, record.Status);

            // Schedule cleanup
            _ = CleanupExecutionAsync(record.ExecutionId);

            // Process next in queue（仅由此处启动，避免与 ProcessQueueAsync while 竞态双跑）
            if (nextExecution != null)
            {
                _ = ProcessExecutionAsync(nextExecution);
            }
            else if (queue.CurrentExecution == null && !queue.HasQueued)
            {
                // 仅当队列完全空闲才清理事件缓冲：
                // CancelAsync 已把 current 推进到下一项时，CompleteAsync 会返回 null，
                // 此时不得清缓冲，否则会丢下一项已发布的事件。
                _eventPublisher.ClearBuffer(record.SessionId);
            }
        }
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
    /// Builds the execution context for an execution.
    /// </summary>
    private async Task<ChatExecutionContext> BuildExecutionContextAsync(
        SessionData session,
        ExecutionRecord record,
        IAgentRegistry agentRegistry,
        AgentSelectionResolver agentSelectionResolver,
        IModelManager modelManager,
        IWorkspaceProvider workspaceProvider,
        IExecutionWorld executionWorld,
        IServiceProvider services)
    {
        var agentId = await agentSelectionResolver.ResolveAgentIdAsync(
            record.Options?.AgentId,
            session.SelectedAgent,
            CancellationToken.None).ConfigureAwait(false);

        var agentDef = await agentRegistry.GetAgentAsync(agentId)
            ?? throw new InvalidOperationException($"Agent '{agentId}' not found");

        if (agentDef.Disabled)
            throw new InvalidOperationException($"Agent '{agentId}' is disabled");

        // 执行级权限授权器：经 IPermissionAuthorizerFactory 构造，
        // 覆盖值来自 ChatOptions.AutoApprove（null=FollowGlobal）；实时生效策略由授权器/引擎解析。
        var permissionAuthorizer = ResolvePermissionAuthorizer(
            services, record.SessionId, record.Options?.AutoApprove);

        var sessionModelRef = modelManager.GetSessionModelRef(session);
        var sessionModelRefOrNull = string.IsNullOrEmpty(sessionModelRef) ? null : sessionModelRef;

        string? requestModelId = agentDef.Runtime == AgentRuntime.AcpPassthrough
            ? modelManager.ResolveAcpModel(record.Options?.ModelId, sessionModelRefOrNull)
            : modelManager.ResolveNativeModel(record.Options?.ModelId, sessionModelRefOrNull, agentId);

        var acpModeId = agentSelectionResolver.ResolveAcpModeId(
            record.Options?.ModeId,
            session.SelectedAcpMode);

        var projectRoot = workspaceProvider.GetProjectRoot();
        var cwd = record.Options?.WorkingDirectory
            ?? session.WorkingDirectory
            ?? executionWorld.Cwd;

        var (settlement, toolSchemas, sectionIds) = await SettleSessionAndSchemasAsync(
            session, agentDef, services).ConfigureAwait(false);

        string? requestThinkingEffort = record.Options?.ThinkingEffort;
        if (requestThinkingEffort is null && !string.IsNullOrWhiteSpace(session.SelectedThinkingEffort))
            requestThinkingEffort = session.SelectedThinkingEffort;

        return new ChatExecutionContext
        {
            SessionId = record.SessionId,
            Agent = agentDef,
            History = new List<ChatMessage>(),
            WorkingDirectory = cwd,
            WorkspaceRoot = projectRoot,
            PermissionAuthorizer = permissionAuthorizer,
            ChannelId = record.Options?.ChannelId,
            UserId = record.Options?.UserId,
            AcpModeId = acpModeId,
            RequestModelId = requestModelId,
            RequestThinkingEffort = requestThinkingEffort,
            Settlement = settlement,
            ToolSchemas = toolSchemas,
            SectionIds = sectionIds
        };
    }

    /// <summary>
    /// 会话级结算 + schema 唯一计算。无 <see cref="IModuleCatalog"/> 时 settled 为空集（schema 亦空）。
    /// </summary>
    private async Task<(SessionSettlementSnapshot? Settlement, IReadOnlyList<FunctionToolSchema> ToolSchemas, IReadOnlyList<string> SectionIds)>
        SettleSessionAndSchemasAsync(
            SessionData session,
            AgentDefinition agent,
            IServiceProvider services)
    {
        var catalog = services.GetService<IModuleCatalog>();
        var toolManager = services.GetService<IToolManager>();
        var options = _seeingAgentOptions.CurrentValue;
        var processScenario = options.Scenario
            ?? services.GetService<ProcessSettlementOptions>()?.HostDefaultScenario;
        var scenarioCatalog = _scenarioCatalog ?? services.GetService<IScenarioCatalog>();

        SessionSettlementSnapshot? settlement = null;
        IReadOnlyList<string> settledToolIds = Array.Empty<string>();

        if (catalog != null)
        {
            settlement = SessionSettlement.Compute(
                session,
                catalog,
                processScenario,
                options.Modules?.Tools?.Disabled,
                resolveScenario: scenarioCatalog is not null
                    ? name => scenarioCatalog.Get(name)
                    : null);
            settledToolIds = settlement.SettledToolIds;

            _logger.LogDebug(
                "会话级结算: session={SessionId}, scenario={Scenario}, modules={ModuleCount}, tools={ToolCount}",
                session.Id,
                settlement.ScenarioName,
                settlement.EnabledModules.Count,
                settlement.SettledToolIds.Count);
        }
        else
        {
            _logger.LogDebug(
                "IModuleCatalog 未注册，跳过会话级结算（schema 空集）: {SessionId}",
                session.Id);
        }

        IReadOnlyList<FunctionToolSchema> toolSchemas = Array.Empty<FunctionToolSchema>();
        if (toolManager != null)
        {
            toolSchemas = await toolManager.GetToolSchemasAsync(settledToolIds, agent)
                .ConfigureAwait(false);
        }

        var sectionIds = services.GetServices<IPromptSectionContributor>()
            .Select(c => c.SectionName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return (settlement, toolSchemas, sectionIds);
    }

    /// <summary>
    /// 构造本执行级权限授权器（经 <see cref="IPermissionAuthorizerFactory"/>）。
    /// <para>
    /// <paramref name="autoApprove"/> 为执行级冻结覆盖（来自 <c>ChatOptions.AutoApprove</c>）；
    /// null 表示 FollowGlobal，由授权器/引擎实时解析会话三态与全局开关。
    /// </para>
    /// </summary>
    internal IPermissionAuthorizer? ResolvePermissionAuthorizer(
        IServiceProvider services,
        string sessionId,
        SessionAutoApprove? autoApprove)
    {
        var factory = services.GetService<IPermissionAuthorizerFactory>();
        if (factory is null)
        {
            _logger.LogWarning(
                "IPermissionAuthorizerFactory 未注册，本次执行无权限授权器: SessionId={SessionId}",
                sessionId);
            return null;
        }

        return factory.Create(sessionId, autoApprove);
    }

    /// <summary>
    /// 是否应在服务端落盘（UI 不负责 Save）。
    /// </summary>
    private static bool ShouldPersistEvent(IMessageEvent evt) => evt switch
    {
        SchemaSnapshotEvent => true,
        StreamCompleteEvent => true,
        ToolCallEvent { Status: ToolCallStatus.Pending or ToolCallStatus.Success
            or ToolCallStatus.Failed or ToolCallStatus.Rejected or ToolCallStatus.Cancelled } => true,
        LoopCompleteEvent or LoopCancelledEvent or ErrorEvent => true,
        _ => false
    };

    /// <summary>
    /// Fills session ChannelId/UserId from inbound values only when session fields are empty.
    /// Never overwrites existing non-whitespace values.
    /// </summary>
    /// <returns>True if either field was updated.</returns>
    public static bool TryBackfillSessionOutbound(SessionData session, string? channelId, string? userId)
    {
        ArgumentNullException.ThrowIfNull(session);

        var changed = false;
        if (string.IsNullOrWhiteSpace(session.ChannelId) && !string.IsNullOrWhiteSpace(channelId))
        {
            session.ChannelId = channelId.Trim();
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(session.UserId) && !string.IsNullOrWhiteSpace(userId))
        {
            session.UserId = userId.Trim();
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Persists inbound model/mode from <see cref="ChatOptions"/> onto the session before execution.
    /// </summary>
    public static bool ApplyInboundModelAndMode(
        SessionData session,
        string? modelId,
        string? modeId,
        IModelManager modelManager)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(modelManager);

        var changed = false;
        if (!string.IsNullOrWhiteSpace(modelId))
            changed |= modelManager.ApplyModelToSession(session, modelId);

        changed |= TryBackfillSessionAcpMode(session, modeId);

        if (changed)
            session.UpdatedAt = DateTime.Now;

        return changed;
    }

    /// <summary>
    /// Updates session ACP mode when provided in inbound options.
    /// </summary>
    public static bool TryBackfillSessionAcpMode(SessionData session, string? modeId)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (string.IsNullOrWhiteSpace(modeId))
            return false;

        var trimmed = modeId.Trim();
        if (string.Equals(session.SelectedAcpMode ?? string.Empty, trimmed, StringComparison.Ordinal))
            return false;

        session.SelectedAcpMode = trimmed;
        return true;
    }

    /// <summary>
    /// 从输入提取用于标题生成的文本（优先 Text，否则拼接文本 Parts）。
    /// </summary>
    private static string? ResolveTitleSourceText(ChatInput? input)
    {
        if (input == null)
            return null;

        if (!string.IsNullOrWhiteSpace(input.Text))
            return input.Text;

        return null;
    }

    /// <summary>
    /// Fire-and-forget 标题确保；独立于主执行取消。
    /// </summary>
    private async Task EnsureTitleFireAndForgetAsync(
        ISessionTitleService ensuring,
        string sessionId,
        string userText,
        string? fallbackModel)
    {
        try
        {
            var title = await ensuring.TryEnsureAsync(
                sessionId,
                userText,
                fallbackModel,
                CancellationToken.None);

            if (!string.IsNullOrEmpty(title))
            {
                _eventPublisher.Publish(sessionId, new SessionTitleChangedEvent
                {
                    SessionId = sessionId,
                    Title = title
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Title ensure failed: SessionId={SessionId}", sessionId);
        }
    }

    /// <summary>
    /// 判定是否为命令输入（/ 开头、至少一个有效字符、非 // 转义）。
    /// </summary>
    private static bool IsCommandInput([NotNullWhen(true)] string? text)
        => text != null
           && text.Length > 1
           && text[0] == '/'
           && !text.StartsWith("//", StringComparison.Ordinal);

    /// <summary>
    /// 移除命令消息（按输入文本匹配最后一条 user 消息，命令文本不得残留在会话中）。
    /// </summary>
    private static void RemoveCommandMessage(SessionData session, string? inputText)
    {
        session.RemoveLastMessage(m =>
            string.Equals(m.Content, inputText, StringComparison.Ordinal));
    }

    /// <summary>
    /// Builds user message from input.
    /// </summary>
    private static SessionMessage BuildUserMessage(ChatInput input)
    {
        var parts = new List<SessionContentPart>();

        if (!string.IsNullOrWhiteSpace(input.Text))
        {
            parts.Add(SessionContentPart.CreateText(input.Text));
        }

        if (input.Attachments != null && input.Attachments.Count > 0)
        {
            foreach (var att in input.Attachments)
            {
                if (att.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    parts.Add(SessionContentPart.CreateImageFromBase64(att.Base64Data, att.MimeType));
                }
                else
                {
                    parts.Add(SessionContentPart.CreateFileFromBase64(att.Base64Data, att.MimeType, att.FileName));
                }
            }
        }

        return parts.Count > 1 || (input.Attachments != null && input.Attachments.Count > 0)
            ? SessionMessage.UserMessageWithParts(parts)
            : SessionMessage.UserMessage(input.Text ?? "");
    }

    /// <summary>
    /// Builds history from session.
    /// </summary>
    internal static List<ChatMessage> BuildHistoryFromSession(SessionData session)
    {
        var history = new List<ChatMessage>();

        // 统一消息来源：仅活跃消息（已压缩标记的旧消息保留展示但不传递给 LLM）
        // 跳过旧版空 system 仅承载 schema_snapshot 的载体（不进 LLM 历史）
        foreach (var msg in session.GetActiveMessages())
        {
            if (IsSchemaSnapshotCarrierMessage(msg))
                continue;

            if (IsRuntimeNotificationMessage(msg))
                continue;

            var chatMessage = new ChatMessage
            {
                Role = msg.Role,
                Content = msg.Content,
                ReasoningContent = msg.ReasoningContent,
                ReasoningSignature = msg.ReasoningSignature,
                ToolCallId = msg.ToolCallId
            };

            if (msg.Parts != null && msg.Parts.Count > 0)
            {
                chatMessage.Parts = msg.Parts.Select(p => new ChatContentPart
                {
                    Type = p.Type,
                    Text = p.Text,
                    Url = p.Url,
                    DataBase64 = p.DataBase64,
                    MimeType = p.MimeType,
                    FileName = p.FileName
                }).ToList();
            }

            if (msg.ToolCalls != null && msg.ToolCalls.Count > 0)
            {
                chatMessage.ToolCalls = msg.ToolCalls.Select(tc => new ToolCall
                {
                    Id = tc.Id,
                    Type = tc.Type,
                    Function = new FunctionCall
                    {
                        Name = tc.Name,
                        Arguments = tc.Arguments
                    }
                }).ToList();
            }

            history.Add(chatMessage);

            // 工具结果在会话中内嵌于 assistant 消息的 ToolCalls[].Result，
            // 此处展开为独立的 tool 消息，满足 OpenAI 对 assistant(tool_calls)
            // 后必须紧跟对应 tool 消息的要求，否则重建历史会触发 400。
            if (msg.ToolCalls is { Count: > 0 })
            {
                foreach (var tc in msg.ToolCalls)
                {
                    history.Add(new ChatMessage
                    {
                        Role = ChatRole.Tool,
                        ToolCallId = tc.Id,
                        Content = ToolResultFormatting.ToModelContent(
                            string.Equals(tc.Status, "success", StringComparison.OrdinalIgnoreCase),
                            tc.Result,
                            tc.Error,
                            tc.Name,
                            tc.Status,
                            tc.Title)
                    });
                }
            }
        }

        return history;
    }

    /// <summary>
    /// 旧版空 system 仅承载 schema_snapshot，不进入 LLM 历史。
    /// </summary>
    private static bool IsSchemaSnapshotCarrierMessage(SessionMessage msg)
    {
        if (!string.Equals(msg.Role, MessageRole.System, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrEmpty(msg.Content))
            return false;
        if (msg.Parts is { Count: > 0 })
            return false;
        return msg.Metadata?.ContainsKey(ChatEventTracker.SchemaSnapshotMetadataKey) == true;
    }

    /// <summary>
    /// 运行时通知（错误/取消）不进 LLM 历史。
    /// 新数据已不再产生；此处兼容存量消息（前缀）与显式 transient 标记。
    /// </summary>
    private static bool IsRuntimeNotificationMessage(SessionMessage msg)
    {
        if (!string.Equals(msg.Role, MessageRole.System, StringComparison.OrdinalIgnoreCase))
            return false;

        if (msg.Metadata?.TryGetValue("transient", out var flag) == true)
        {
            if (flag is bool b && b)
                return true;
            // 落盘再加载后 Metadata 值为 JsonElement，需兼容
            if (flag is System.Text.Json.JsonElement je
                && je.ValueKind == System.Text.Json.JsonValueKind.True)
                return true;
        }

        if (string.IsNullOrEmpty(msg.Content))
            return false;

        return msg.Content.StartsWith("错误: ", StringComparison.Ordinal)
            || msg.Content.StartsWith("对话已取消: ", StringComparison.Ordinal)
            || msg.Content.StartsWith("压缩失败", StringComparison.Ordinal)
            || msg.Content.StartsWith("⚠️ 执行已取消", StringComparison.Ordinal)
            || msg.Content.StartsWith("❌ 执行出错", StringComparison.Ordinal);
    }

    /// <summary>
    /// Builds agent context from execution context.
    /// </summary>
    private static AgentContext BuildAgentContext(ChatExecutionContext context, CancellationToken cancellationToken)
    {
        var agentContext = new AgentContext
        {
            SessionId = context.SessionId,
            WorkingDirectory = context.WorkingDirectory ?? context.WorkspaceRoot ?? "",
            WorkspaceRoot = context.WorkspaceRoot ?? "",
            PermissionAuthorizer = context.PermissionAuthorizer,
            CancellationToken = cancellationToken,
            ToolSchemas = context.ToolSchemas ?? Array.Empty<FunctionToolSchema>()
        };

        // 传递请求级模型选择到 Metadata（适用于 Native Agent 和 ACP Passthrough）
        // 优先级：用户选择 > Agent 配置 > 全局默认
        if (!string.IsNullOrEmpty(context.RequestModelId))
            agentContext.Metadata[AgentContextKeys.RequestModelId] = context.RequestModelId;

        if (context.RequestThinkingEffort is not null)
            agentContext.Metadata[AgentContextKeys.RequestThinkingEffort] = context.RequestThinkingEffort;

        if (!string.IsNullOrEmpty(context.AcpModeId))
            agentContext.Metadata[AgentContextKeys.AcpModeId] = context.AcpModeId;

        return agentContext;
    }

    /// <summary>
    /// Processes a command during execution.
    /// </summary>
    private async IAsyncEnumerable<IMessageEvent?> ProcessCommandAsync(
        string sessionId,
        string input,
        SessionData session,
        ChatExecutionContext context,
        ICommandRegistry commandRegistry,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var cmdName = input.Split(' ').FirstOrDefault()?.TrimStart('/') ?? "";
        var currentRuntime = context.Agent.Runtime;

        // 按 Runtime 查找命令
        var command = commandRegistry.GetCommand(cmdName, currentRuntime);
        if (command == null)
        {
            // 没有匹配的命令，透传给 Agent
            yield return null;
            yield break;
        }

        // 直接执行命令（薄上下文：仅信息传递，命令自包含会话操作，宿主不做写同步）
        var cmdContext = new CommandContext
        {
            CommandName = cmdName,
            Input = input,
            Arguments = input.Contains(' ') ? input.Substring(input.IndexOf(' ') + 1) : "",
            SessionId = sessionId,
            WorkspaceRoot = context.WorkspaceRoot,
            CancellationToken = cancellationToken
        };

        var result = await command.ExecuteAsync(cmdContext, cancellationToken);

        // 命令产生操作结果声明是否移除命令消息（默认移除），由宿主统一处理；
        // 命令可返回 false 保留（如 Skill 展开后把命令消息替换为技能内容）。
        // 未知命令不在此路径（command==null 时文本保留并作为普通消息透传）。
        if (result.RemoveCommandMessage)
        {
            RemoveCommandMessage(session, input);
        }

        // 根据 CommandResult 决定是否继续
        if (!result.ShouldContinue || result.ShouldExit)
        {
            yield return new CommandResultEvent
            {
                SessionId = sessionId,
                CommandName = cmdName,
                Success = result.Success,
                Message = result.Success ? result.Message : result.ErrorMessage,
                NavigationTarget = result.GetNavigationTarget(),
                NeedsRefresh = result.NeedsRefresh,
                // 短路标志：宿主据此跳过 Agent 执行，不再把命令文本发给大模型
                ShouldContinue = result.ShouldContinue && !result.ShouldExit
            };
            yield break;
        }

        // 继续执行 Agent
        yield return null;
    }

    /// <summary>
    /// 标记会话为 Error 状态（执行失败时调用）。
    /// 仅标记 SubAgent 子会话：task_status 回落判定依赖 SessionStatus，而 Root/Fork 会话无此消费方。
    /// 子会话执行失败后，ExecutionRecord 终态很快从队列移除，仅靠执行记录无法长期判定失败。
    /// </summary>
    private void MarkSessionError(ISessionManager sessionManager, string sessionId, string? errorMessage)
    {
        try
        {
            var failedSession = sessionManager.Get(sessionId);
            if (failedSession != null && failedSession.Kind == SessionKind.SubAgent)
            {
                failedSession.Status = SessionStatus.Error;
                failedSession.UpdatedAt = DateTime.Now;
                if (!string.IsNullOrEmpty(errorMessage))
                    failedSession.Metadata[SessionMetadataKeys.LastError] = errorMessage;
            }
        }
        catch (Exception statusEx)
        {
            _logger.LogWarning(statusEx, "标记会话 Error 状态失败: {SessionId}", sessionId);
        }
    }

    /// <summary>
    /// 子会话失败标记 Error 后，成功续跑（传递 task_id 继续任务）时清除 Error 标记。
    /// 否则子会话一旦失败，即使后续成功续跑，task_status 仍会误判为 error。
    /// </summary>
    private void ClearSessionError(ISessionManager sessionManager, string sessionId)
    {
        try
        {
            var session = sessionManager.Get(sessionId);
            if (session != null &&
                session.Kind == SessionKind.SubAgent &&
                session.Status == SessionStatus.Error)
            {
                session.Status = SessionStatus.Active;
                session.Metadata.Remove(SessionMetadataKeys.LastError);
                session.UpdatedAt = DateTime.Now;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "清除会话 Error 状态失败: {SessionId}", sessionId);
        }
    }

    /// <summary>
    /// Appends execution history to session metadata.
    /// </summary>
    private async Task AppendExecutionHistoryAsync(ISessionManager sessionManager, ExecutionRecord record)
    {
        var session = sessionManager.Get(record.SessionId);
        if (session == null) return;

        var historyJson = session.Metadata.GetValueOrDefault("execution_history", "[]");
        var history = JsonSerializer.Deserialize<List<ExecutionHistoryEntry>>(historyJson) ?? new();

        history.Add(new ExecutionHistoryEntry
        {
            ExecutionId = record.ExecutionId,
            Status = record.Status,
            StartedAt = record.StartedAt,
            CompletedAt = record.CompletedAt,
            ErrorMessage = record.ErrorMessage
        });

        // Limit history size
        if (history.Count > _options.ExecutionHistoryLimit)
        {
            history = history.TakeLast(_options.ExecutionHistoryLimit).ToList();
        }

        session.Metadata["execution_history"] = JsonSerializer.Serialize(history);
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
            // Skip if has active execution
            if (queue.HasActiveExecution || queue.HasQueued)
                continue;

            // Check idle timeout
            if (now - queue.LastActiveTime > _options.SessionIdleTimeout)
            {
                sessionsToRemove.Add(sessionId);
            }
        }

        foreach (var sessionId in sessionsToRemove)
        {
            if (!_sessionQueues.TryRemove(sessionId, out var queue))
                continue;

            // 竞态防护：移除瞬间可能被并发 SubmitAsync 变为活跃（或其排队项尚未处理）。
            // 此时放回字典，交由后续清理周期处理，避免误弃在途/排队执行导致其永不推进。
            if (queue.HasActiveExecution || queue.HasQueued)
            {
                _sessionQueues.TryAdd(sessionId, queue);
                continue;
            }

            queue.Dispose();
            _eventPublisher.CompleteSession(sessionId);
            _logger.LogDebug("Cleaned up idle session queue: {SessionId}", sessionId);
        }
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
    /// Disposes all resources.
    /// 同步兼容入口：内部委托 <see cref="DisposeAsync"/>；宿主支持时应优先调用异步释放。
    /// </summary>
    public void Dispose()
        => DisposeAsync().AsTask().ConfigureAwait(false).GetAwaiter().GetResult();

    /// <summary>
    /// 异步释放全部资源：取消所有在途执行并释放各执行记录的 CTS。
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

            queue.Dispose();
        }
        _sessionQueues.Clear();

        // 释放终态执行记录的 CTS：含“已取消并被推出队列、不再被队列引用”的记录，避免泄漏。
        // 仍在途的记录由其 ProcessExecutionAsync 的 finally → CompleteAsync 负责释放。
        foreach (var record in _executions.Values)
        {
            if (record.IsTerminal)
            {
                record.Cts?.Dispose();
                record.Cts = null;
            }
        }
        _executions.Clear();

        _logger.LogInformation("ExecutionJobService disposed");
    }
}

/// <summary>
/// Entry for execution history.
/// </summary>
public class ExecutionHistoryEntry
{
    public string ExecutionId { get; set; } = "";
    public ExecutionStatus Status { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
}
/// <summary>Peek DTO for TokenBudget.AutoCompactionEnabled without referencing TokenBudget package.</summary>
internal sealed class TokenBudgetAutoCompactionPeek
{
    public bool AutoCompactionEnabled { get; set; } = true;
}
