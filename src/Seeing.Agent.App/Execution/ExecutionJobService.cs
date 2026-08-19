using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Llm;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Core.Permission;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.App.Events;
using Seeing.Agent.App.Internal;
using Seeing.Agent.App.Models;
using Seeing.Agent.Commands;
using Seeing.Agent.Compression;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Core.Instructions;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Core.Background;
using Seeing.Agent.Core.Scheduling;
using Seeing.Agent.Services;
using Seeing.Session.Core;
using Seeing.Session.Management;

namespace Seeing.Agent.App.Execution;

/// <summary>
/// Background execution service that manages execution jobs independently of UI connections.
/// Supports queuing per session, event streaming, and automatic cleanup.
/// </summary>
public class ExecutionJobService : IDisposable
{
    private readonly ConcurrentDictionary<string, SessionExecutionQueue> _sessionQueues = new();
    private readonly ConcurrentDictionary<string, ExecutionRecord> _executions = new();
    private readonly ConcurrentDictionary<string, CircularBuffer<IMessageEvent>> _eventBuffers = new();
    private readonly IServiceProvider _serviceProvider;
    private readonly IExecutionEventPublisher _eventPublisher;
    private readonly ExecutionOptions _options;
    private readonly IOptionsMonitor<SeeingAgentOptions> _seeingAgentOptions;
    private readonly CompressionService _compressionService;
    private readonly ILogger<ExecutionJobService> _logger;
    private readonly Timer _cleanupTimer;
    private readonly IAgentLoopScheduler? _loopScheduler;
    private readonly IBackgroundTaskManager? _backgroundTasks;
    private bool _disposed;

    /// <summary>
    /// Creates a new ExecutionJobService.
    /// </summary>
    public ExecutionJobService(
        IServiceProvider serviceProvider,
        IExecutionEventPublisher eventPublisher,
        ExecutionOptions options,
        IOptionsMonitor<SeeingAgentOptions> seeingAgentOptions,
        ILogger<ExecutionJobService> logger,
        CompressionService compressionService,
        IAgentLoopScheduler? loopScheduler = null,
        IBackgroundTaskManager? backgroundTasks = null)
    {
        _serviceProvider = serviceProvider;
        _eventPublisher = eventPublisher;
        _options = options ?? new ExecutionOptions();
        _seeingAgentOptions = seeingAgentOptions;
        _compressionService = compressionService ?? throw new ArgumentNullException(nameof(compressionService));
        _logger = logger;
        _loopScheduler = loopScheduler;
        _backgroundTasks = backgroundTasks;

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
    public async Task<ExecutionSubmitResult> SubmitAsync(string sessionId, ChatInput input, ChatOptions? options)
    {
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
            var session = await sessionManager.EnsureSessionAsync(sessionId);
            TryBackfillSessionOutbound(session, options?.ChannelId, options?.UserId);

            // ⭐ Persist model/mode selection to session (ensures they're saved even if execution fails)
            ApplyInboundModelAndMode(session, options?.ModelId, options?.ModeId, modelManager);

            var cwd = options?.WorkingDirectory
                ?? session.WorkingDirectory
                ?? workspaceProvider.WorkspaceRoot;
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
                        workspaceProvider.WorkspaceRoot,
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
                session.Messages.Add(userMessage);
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

        // Submit to queue
        await queue.SubmitAsync(record);
        _executions[executionId] = record;

        // Start processing if not already running
        _ = ProcessQueueAsync(sessionId);

        var result = record.Status == ExecutionStatus.Queued
            ? ExecutionSubmitResult.Queued(executionId, record.QueuePosition)
            : ExecutionSubmitResult.Succeeded(executionId);

        _logger.LogInformation("Execution {ExecutionId} submitted with status {Status}", executionId, record.Status);

        return result;
    }

    /// <summary>
    /// Cancels an execution.
    /// </summary>
    /// <param name="executionId">The execution ID to cancel.</param>
    /// <returns>True if cancelled, false if not found or already terminal.</returns>
    public bool Cancel(string executionId)
    {
        if (!_executions.TryGetValue(executionId, out var record))
            return false;

        if (record.IsTerminal)
            return false;

        if (!_sessionQueues.TryGetValue(record.SessionId, out var queue))
            return false;

        var cancelled = Task.Run(() => queue.CancelAsync(executionId)).GetAwaiter().GetResult();

        // 无论队列取消是否成功，都级联取消该会话下未完成的后台 Task
        try
        {
            var btm = _backgroundTasks
                ?? _serviceProvider.GetService(typeof(IBackgroundTaskManager)) as IBackgroundTaskManager;
            Task.Run(() => btm?.CancelBySessionAsync(record.SessionId)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "取消会话后台任务失败: {SessionId}", record.SessionId);
        }

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
        var commandRegistry = scope.ServiceProvider.GetRequiredService<ICommandRegistry>();

        var queue = _sessionQueues[record.SessionId];

        // Mark as running（若已被取消/推进则返回 false，避免把已终态记录重新置 Running）
        if (!await queue.StartAsync())
            return;
        record.StartedAt = DateTime.UtcNow;
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
            var session = await sessionManager.EnsureSessionAsync(record.SessionId);

            // 自动压缩门控：TokenBudget 标记 + 配置开启时，每轮 Agent 循环开始前触发压缩
            var autoCompaction = _seeingAgentOptions.CurrentValue.TokenBudget?.AutoCompactionEnabled == true;
            if (autoCompaction &&
                session.PendingCompaction &&
                session.Messages.Count > 0)
            {
                var outcome = await _compressionService.CompressAsync(session.Id, reason: "auto");
                await PublishCompactionEventsAsync(session.Id, outcome);
                session.PendingCompaction = false;
            }

            // Build execution context with background permission channel
            var context = await BuildExecutionContextAsync(
                session, record, agentRegistry, agentSelectionResolver, modelManager, workspaceProvider);

            // 旁路生成标题（不阻塞主对话）
            if (record.Options?.SkipUserMessagePersist != true)
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
            if (inputText != null && inputText.StartsWith('/') && inputText.Length > 1 && !inputText.StartsWith("//"))
            {
                await foreach (var cmdEvent in ProcessCommandAsync(
                    record.SessionId, inputText, session, context, commandRegistry, queue.CurrentCancellationToken))
                {
                    if (cmdEvent != null)
                    {
                        _eventPublisher.Publish(record.SessionId, cmdEvent);
                    }
                }
            }

            // Build history
            var messages = BuildHistoryFromSession(session);

            // 服务端负责将事件投影到 Session 并落盘；UI 只订阅展示
            var eventTracker = new ChatEventTracker();

            // Execute agent
            await foreach (var evt in executionRouter.ExecuteAsync(
                context.Agent,
                messages,
                BuildAgentContext(context, queue.CurrentCancellationToken),
                queue.CurrentCancellationToken))
            {
                // Check for cancellation
                if (queue.CurrentCancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException();

                var liveSession = sessionManager.Get(record.SessionId) ?? session;
                eventTracker.ApplyEvent(liveSession, evt);
                TaskSessionProjector.Apply(liveSession, evt);

                // 先投影再发布，保证 UI 读到的是已写入的 SessionData
                _eventPublisher.Publish(record.SessionId, evt);

                if (ShouldPersistEvent(evt))
                    await sessionManager.SaveAsync(record.SessionId);
            }

            // Mark as completed
            record.Status = ExecutionStatus.Completed;
            _logger.LogInformation("Execution {ExecutionId} completed successfully", record.ExecutionId);
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
            var nextExecution = await queue.CompleteAsync(record.ExecutionId, record.Status);

            // Schedule cleanup
            _ = CleanupExecutionAsync(record.ExecutionId);

            // Process next in queue（仅由此处启动，避免与 ProcessQueueAsync while 竞态双跑）
            if (nextExecution != null)
            {
                _ = ProcessExecutionAsync(nextExecution);
            }
            else
            {
                // 队列为空才清理事件缓冲，避免清掉下一项已发布的事件
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
        IWorkspaceProvider workspaceProvider)
    {
        var agentId = await agentSelectionResolver.ResolveAgentIdAsync(
            record.Options?.AgentId,
            session.SelectedAgent,
            CancellationToken.None).ConfigureAwait(false);

        var agentDef = await agentRegistry.GetAgentAsync(agentId)
            ?? throw new InvalidOperationException($"Agent '{agentId}' not found");

        if (agentDef.Disabled)
            throw new InvalidOperationException($"Agent '{agentId}' is disabled");

        // 权限通道选择优先级：
        // 1. 会话级 Enabled 时使用 AutoApproveInstance（强制自动批准）
        // 2. 会话级 Disabled 时强制使用调用方通道（WebUI 交互式），否则 DenyAll
        // 3. 会话级 FollowGlobal 时走全局配置：AutoApproveAll=true 用 AutoApproveInstance，否则调用方通道/DenyAll
        var permissionChannel = ResolvePermissionChannel(
            record.Options?.PermissionChannel,
            record.Options?.AutoApprove ?? SessionAutoApprove.FollowGlobal);

        var sessionModelRef = modelManager.GetSessionModelRef(session);
        var sessionModelRefOrNull = string.IsNullOrEmpty(sessionModelRef) ? null : sessionModelRef;

        string? requestModelId = agentDef.Runtime == AgentRuntime.AcpPassthrough
            ? modelManager.ResolveAcpModel(record.Options?.ModelId, sessionModelRefOrNull)
            : modelManager.ResolveNativeModel(record.Options?.ModelId, sessionModelRefOrNull, agentId);

        var acpModeId = agentSelectionResolver.ResolveAcpModeId(
            record.Options?.ModeId,
            session.SelectedAcpMode);

        return new ChatExecutionContext
        {
            SessionId = record.SessionId,
            Agent = agentDef,
            History = new List<ChatMessage>(),
            WorkingDirectory = record.Options?.WorkingDirectory ?? workspaceProvider.WorkspaceRoot,
            WorkspaceRoot = workspaceProvider.WorkspaceRoot,
            PermissionChannel = permissionChannel,
            ChannelId = record.Options?.ChannelId,
            UserId = record.Options?.UserId,
            AcpModeId = acpModeId,
            RequestModelId = requestModelId
        };
    }

    /// <summary>
    /// 根据配置解析权限通道。
    /// <para>
    /// 优先级：
    /// 1. 会话级 Enabled 时使用 AutoApproveInstance（强制自动批准）
    /// 2. 会话级 Disabled 时强制使用调用方通道（WebUI 传递 BlazorPermissionChannel），无调用方则 DenyAll
    /// 3. 会话级 FollowGlobal 时走全局配置：AutoApproveAll=true 用 AutoApproveInstance，否则调用方通道/DenyAll
    /// </para>
    /// </summary>
    /// <param name="callerChannel">调用方传递的权限通道（可选）</param>
    /// <param name="autoApprove">会话级自动批准策略（默认跟随全局）</param>
    internal IPermissionChannel ResolvePermissionChannel(
        IPermissionChannel? callerChannel,
        SessionAutoApprove autoApprove)
    {
        // 会话级强制自动批准
        if (autoApprove == SessionAutoApprove.Enabled)
        {
            _logger.LogInformation(
                "Using AutoApprove permission channel (session-level AutoApprove=Enabled)");
            return DefaultPermissionChannel.AutoApproveInstance;
        }

        // 会话级强制交互式确认：优先调用方通道，否则拒绝
        if (autoApprove == SessionAutoApprove.Disabled)
        {
            if (callerChannel != null)
            {
                _logger.LogInformation(
                    "Using caller-provided permission channel (session-level AutoApprove=Disabled, {ChannelType})",
                    callerChannel.GetType().Name);
                return callerChannel;
            }

            _logger.LogInformation(
                "Using DenyAll permission channel (session-level AutoApprove=Disabled, no caller channel)");
            return DenyAllPermissionChannel.Instance;
        }

        // 跟随全局配置：AutoApproveAll=true 优先级最高
        var autoApproveAll = _seeingAgentOptions.CurrentValue.Permission?.AutoApproveAll ?? false;

        if (autoApproveAll)
        {
            _logger.LogInformation(
                "Using AutoApprove permission channel (AutoApproveAll=true, overriding caller channel)");

            // 使用自动批准的权限通道
            return DefaultPermissionChannel.AutoApproveInstance;
        }

        // 调用方传递的权限通道
        if (callerChannel != null)
        {
            _logger.LogInformation(
                "Using caller-provided permission channel ({ChannelType})",
                callerChannel.GetType().Name);
            return callerChannel;
        }

        _logger.LogInformation(
            "Using DenyAll permission channel (no caller channel, AutoApproveAll=false)");

        // 后台执行模式：立即拒绝，不等待超时
        return DenyAllPermissionChannel.Instance;
    }

    /// <summary>
    /// 是否应在服务端落盘（UI 不负责 Save）。
    /// </summary>
    private static bool ShouldPersistEvent(IMessageEvent evt) => evt switch
    {
        StreamCompleteEvent => true,
        ToolCallEvent { Status: ToolCallStatus.Pending or ToolCallStatus.Success
            or ToolCallStatus.Failed or ToolCallStatus.Rejected or ToolCallStatus.Cancelled } => true,
        TaskStartedEvent or TaskCompletedEvent or TaskFailedEvent => true,
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

        foreach (var msg in session.Messages)
        {
            var chatMessage = new ChatMessage
            {
                Role = msg.Role,
                Content = msg.Content,
                ReasoningContent = msg.ReasoningContent,
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
                        Content = tc.Result ?? tc.Error ?? string.Empty
                    });
                }
            }
        }

        return history;
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
            PermissionChannel = context.PermissionChannel,
            CancellationToken = cancellationToken
        };

        // 传递请求级模型选择到 Metadata（适用于 Native Agent 和 ACP Passthrough）
        // 优先级：用户选择 > Agent 配置 > 全局默认
        if (!string.IsNullOrEmpty(context.RequestModelId))
            agentContext.Metadata[AgentContextKeys.RequestModelId] = context.RequestModelId;

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

        // 直接执行命令
        var cmdContext = new CommandContext
        {
            CommandName = cmdName,
            RawInput = input,
            Input = input,
            Arguments = input.Contains(' ') ? input.Substring(input.IndexOf(' ') + 1) : "",
            SessionId = sessionId,
            WorkspaceRoot = context.WorkspaceRoot,
            History = context.History,
            CancellationToken = cancellationToken
        };

        var result = await command.ExecuteAsync(cmdContext, cancellationToken);

        // 如果 Input 被修改，更新消息内容
        if (result.Success && cmdContext.Input != cmdContext.RawInput && session.Messages.Count > 0)
        {
            session.Messages[^1].Content = cmdContext.Input;
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
                NeedsRefresh = result.NeedsRefresh
            };
            yield break;
        }

        // 继续执行 Agent
        yield return null;
    }

    /// <summary>
    /// 发布压缩事件（Started → Completed/Failed）
    /// </summary>
    private Task PublishCompactionEventsAsync(string sessionId, CompressionOutcome outcome)
    {
        _eventPublisher.Publish(sessionId, new CompactionStartedEvent
        {
            SessionId = sessionId,
            Reason = "auto"
        });

        if (outcome.Success)
        {
            _eventPublisher.Publish(sessionId, new CompactionCompletedEvent
            {
                SessionId = sessionId,
                TokensBefore = outcome.TokensBefore,
                TokensAfter = outcome.TokensAfter,
                MessagesRemoved = outcome.MessagesRemoved,
                Summary = outcome.Summary
            });
        }
        else
        {
            _eventPublisher.Publish(sessionId, new CompactionFailedEvent
            {
                SessionId = sessionId,
                ErrorMessage = outcome.ErrorMessage
            });
        }

        return Task.CompletedTask;
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
            if (_sessionQueues.TryRemove(sessionId, out var queue))
            {
                queue.Dispose();
                _eventPublisher.CompleteSession(sessionId);
                _logger.LogDebug("Cleaned up idle session queue: {SessionId}", sessionId);
            }
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
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _cleanupTimer.Dispose();

        // Take a snapshot to avoid collection modified exception
        var snapshot = _sessionQueues.ToArray();
        foreach (var (_, queue) in snapshot)
        {
            queue.Dispose();
        }
        _sessionQueues.Clear();
        _executions.Clear();

        _logger.LogInformation("ExecutionJobService disposed");
    }
}

/// <summary>
/// Event fired when execution starts.
/// </summary>
public record ExecutionStartedEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public string? LoopId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string Type => MessageEventType.LoopStart;

    public string ExecutionId { get; init; } = "";
}

/// <summary>
/// Event fired when execution completes.
/// </summary>
public record ExecutionCompleteEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public string? LoopId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string Type => MessageEventType.LoopComplete;

    public string ExecutionId { get; init; } = "";
    public ExecutionStatus Status { get; init; }
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