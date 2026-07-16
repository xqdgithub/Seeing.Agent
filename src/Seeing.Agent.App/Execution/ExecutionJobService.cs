using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seeing.Agent.App.Events;
using Seeing.Agent.App.Internal;
using Seeing.Agent.App.Models;
using Seeing.Agent.Commands;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core;
using Seeing.Agent.Core.Events;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Llm;
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
    private readonly ILogger<ExecutionJobService> _logger;
    private readonly Timer _cleanupTimer;
    private bool _disposed;

    /// <summary>
    /// Creates a new ExecutionJobService.
    /// </summary>
    public ExecutionJobService(
        IServiceProvider serviceProvider,
        IExecutionEventPublisher eventPublisher,
        ExecutionOptions options,
        ILogger<ExecutionJobService> logger)
    {
        _serviceProvider = serviceProvider;
        _eventPublisher = eventPublisher;
        _options = options ?? new ExecutionOptions();
        _logger = logger;

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

        // ⭐ Immediately save user message to session storage
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var sessionManager = scope.ServiceProvider.GetRequiredService<ISessionManager>();
            var session = await sessionManager.EnsureSessionAsync(sessionId);

            var userMessage = BuildUserMessage(input);
            session.Messages.Add(userMessage);

            // Save immediately - this is the key fix for the persistence issue
            await sessionManager.SaveAsync(sessionId);

            _logger.LogDebug("User message saved immediately for execution {ExecutionId}", executionId);
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

        var cancelled = queue.CancelAsync(executionId).GetAwaiter().GetResult();

        if (cancelled)
        {
            _logger.LogInformation("Execution {ExecutionId} cancelled", executionId);
            _eventPublisher.Publish(record.SessionId, new ExecutionCompleteEvent
            {
                SessionId = record.SessionId,
                ExecutionId = executionId,
                Status = ExecutionStatus.Cancelled
            });
            _eventPublisher.CompleteSession(record.SessionId);
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

        while (queue.HasActiveExecution)
        {
            var current = queue.CurrentExecution;
            if (current == null)
                break;

            // Skip if already being processed (race condition guard)
            if (current.Status != ExecutionStatus.Pending)
                break;

            await ProcessExecutionAsync(current);
        }
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
        var executionRouter = scope.ServiceProvider.GetRequiredService<IAgentExecutionRouter>();
        var agentSelectionResolver = scope.ServiceProvider.GetRequiredService<AgentSelectionResolver>();
        var workspaceProvider = scope.ServiceProvider.GetRequiredService<IWorkspaceProvider>();
        var commandDispatcher = scope.ServiceProvider.GetRequiredService<CommandDispatcher>();
        var commandRegistry = scope.ServiceProvider.GetRequiredService<ICommandRegistry>();

        var queue = _sessionQueues[record.SessionId];

        // Mark as running
        await queue.StartAsync();
        record.StartedAt = DateTime.UtcNow;

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

            // Build execution context with background permission channel
            var context = await BuildExecutionContextAsync(
                session, record, agentRegistry, agentSelectionResolver, workspaceProvider);

            // Process command if applicable
            if (record.Input?.Text != null && CommandDispatcher.IsCommand(record.Input.Text))
            {
                await foreach (var cmdEvent in ProcessCommandAsync(
                    record.SessionId, record.Input.Text, session, context, commandRegistry, commandDispatcher, queue.CurrentCancellationToken))
                {
                    if (cmdEvent != null)
                    {
                        _eventPublisher.Publish(record.SessionId, cmdEvent);
                    }
                }
            }

            // Build history
            context.History = BuildHistoryFromSession(session);

            // Execute agent
            await foreach (var evt in executionRouter.ExecuteAsync(
                context.Agent, BuildAgentContext(context, queue.CurrentCancellationToken), queue.CurrentCancellationToken))
            {
                // Check for cancellation
                if (queue.CurrentCancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException();

                // Publish event
                _eventPublisher.Publish(record.SessionId, evt);

                // Incremental save on message complete
                if (evt is StreamCompleteEvent or ToolCallEvent)
                {
                    await sessionManager.SaveAsync(record.SessionId);
                }
            }

            // Mark as completed
            record.Status = ExecutionStatus.Completed;
            _logger.LogInformation("Execution {ExecutionId} completed successfully", record.ExecutionId);
        }
        catch (OperationCanceledException)
        {
            record.Status = ExecutionStatus.Cancelled;
            _logger.LogInformation("Execution {ExecutionId} was cancelled", record.ExecutionId);
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

            // Final save
            try
            {
                await sessionManager.SaveAsync(record.SessionId);
                await AppendExecutionHistoryAsync(sessionManager, record);
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

            // Clear event buffer on terminal state
            _eventPublisher.ClearBuffer(record.SessionId);

            // Complete the execution and start next
            var nextExecution = await queue.CompleteAsync(record.ExecutionId, record.Status);

            // Schedule cleanup
            _ = CleanupExecutionAsync(record.ExecutionId);

            // Process next in queue
            if (nextExecution != null)
            {
                _ = ProcessExecutionAsync(nextExecution);
            }
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
        IWorkspaceProvider workspaceProvider)
    {
        var agentId = record.Options?.AgentId
            ?? await agentSelectionResolver.ResolveAgentIdAsync(null, null, CancellationToken.None);

        var agent = agentRegistry.GetOrCreateAgentInstance(agentId)
            ?? throw new InvalidOperationException($"Agent '{agentId}' not found");

        if (agent.Disabled)
            throw new InvalidOperationException($"Agent '{agentId}' is disabled");

        var agentDef = Core.Models.AgentDefinition.FromAgent(agent);

        return new ChatExecutionContext
        {
            SessionId = record.SessionId,
            Agent = agentDef,
            History = new List<ChatMessage>(),
            WorkingDirectory = record.Options?.WorkingDirectory ?? workspaceProvider.WorkspaceRoot,
            WorkspaceRoot = workspaceProvider.WorkspaceRoot,
            PermissionChannel = new BackgroundPermissionChannel(_options.PermissionTimeout),
            ChannelId = record.Options?.ChannelId,
            UserId = record.Options?.UserId,
            AcpModeId = record.Options?.ModeId,
            RequestModelId = record.Options?.ModelId
        };
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
    private static List<ChatMessage> BuildHistoryFromSession(SessionData session)
    {
        var history = new List<ChatMessage>();

        foreach (var msg in session.Messages)
        {
            var chatMessage = new ChatMessage
            {
                Role = msg.Role,
                Content = msg.Content,
                ReasoningContent = msg.ReasoningContent
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
            History = context.History,
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
        CommandDispatcher commandDispatcher,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var cmdName = input.Split(' ').FirstOrDefault()?.TrimStart('/') ?? "";
        var command = commandRegistry.GetCommand(cmdName);

        if (command == null)
        {
            yield return null;
            yield break;
        }

        var cmdType = command.Metadata.Type;
        var args = input.Contains(' ') ? input.Substring(input.IndexOf(' ') + 1) : "";

        var cmdContext = new CommandContext
        {
            CommandName = cmdName,
            RawInput = input,
            Arguments = args,
            SessionId = sessionId,
            WorkspaceRoot = context.WorkspaceRoot,
            History = context.History,
            CancellationToken = cancellationToken
        };

        var result = await commandDispatcher.HandleAsync(input, cmdContext, cancellationToken);

        if (cmdType == CommandType.System)
        {
            yield return new CommandResultEvent
            {
                SessionId = sessionId,
                CommandName = cmdName,
                Success = result.Success,
                Message = result.Success ? result.Message : result.ErrorMessage
            };
            yield break;
        }

        if (cmdType == CommandType.Skill && result.Success)
        {
            var expandedContent = result.GetExpandedContent();
            if (expandedContent != null && session.Messages.Count > 0)
            {
                session.Messages[^1].Content = expandedContent;
            }
        }

        yield return null;
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
    public MessageEventType Type => MessageEventType.LoopStart;

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
    public MessageEventType Type => MessageEventType.LoopComplete;

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