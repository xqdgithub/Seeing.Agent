using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core;
using Seeing.Agent.Core.Events;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Gateway.Permission;
using Seeing.Agent.Llm;
using Seeing.Gateway.Mapping;
using Seeing.Gateway.Models;
using Seeing.Session.Core;
using Seeing.Session.Management;

namespace Seeing.Agent.Gateway.Core;

/// <summary>
/// Gateway 聊天编排器，镜像 WebUI Session.razor 执行流。
/// </summary>
public sealed class GatewayOrchestrator
{
    private readonly IServiceProvider _services;
    private readonly GatewayOptions _options;
    private readonly AgentSelectionResolver _selectionResolver;
    private readonly GatewayPermissionChannel _permissionChannel;
    private readonly GatewayRunTracker _runTracker;
    private readonly SessionExecutionQueue _executionQueue;
    private readonly GatewaySessionResolver _sessionResolver;
    private readonly GatewayConnectionManager? _connectionManager;
    private readonly ILogger<GatewayOrchestrator> _logger;

    public GatewayOrchestrator(
        IServiceProvider services,
        GatewayOptions options,
        AgentSelectionResolver selectionResolver,
        GatewayPermissionChannel permissionChannel,
        GatewayRunTracker runTracker,
        SessionExecutionQueue executionQueue,
        GatewaySessionResolver sessionResolver,
        ILogger<GatewayOrchestrator> logger,
        GatewayConnectionManager? connectionManager = null)
    {
        _services = services;
        _options = options;
        _selectionResolver = selectionResolver;
        _permissionChannel = permissionChannel;
        _runTracker = runTracker;
        _executionQueue = executionQueue;
        _sessionResolver = sessionResolver;
        _connectionManager = connectionManager;
        _logger = logger;
    }

    /// <summary>执行聊天请求并以 Gateway 事件流返回</summary>
    public async IAsyncEnumerable<GatewayEvent> ExecuteChatAsync(
        GatewayRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channelId = request.ChannelId ?? "default";
        var sessionId = request.SessionId;

        await _executionQueue.WaitAsync(channelId, sessionId, cancellationToken);

        var runCts = _runTracker.RegisterRun(sessionId);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, runCts.Token);

        var runState = new ChatRunState();
        Exception? fault = null;
        var cancelled = false;

        await using var enumerator = StreamChatEventsAsync(request, linkedCts.Token, runState)
            .GetAsyncEnumerator(linkedCts.Token);

        while (true)
        {
            var hasNext = false;
            try
            {
                hasNext = await enumerator.MoveNextAsync();
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                break;
            }
            catch (Exception ex)
            {
                fault = ex;
                break;
            }

            if (!hasNext)
                break;

            yield return enumerator.Current;
        }

        _runTracker.UnregisterRun(sessionId);
        _executionQueue.Release(channelId, sessionId);

        if (cancelled)
            yield return await BuildCancelledEventAsync(sessionId, runState);
        else if (fault != null)
        {
            _logger.LogError(fault, "Gateway 聊天执行失败: SessionId={SessionId}", sessionId);
            yield return await BuildErrorEventAsync(sessionId, runState, fault);
        }
    }

    private async IAsyncEnumerable<GatewayEvent> StreamChatEventsAsync(
        GatewayRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        ChatRunState runState)
    {
        var sessionId = request.SessionId;
        var sessionManager = _services.GetRequiredService<SessionManager>();
        var agentRegistry = _services.GetRequiredService<IAgentRegistry>();
        var executionRouter = _services.GetRequiredService<IAgentExecutionRouter>();
        var workspaceRoot = _services.GetRequiredService<IWorkspaceProvider>().WorkspaceRoot;
        var sessionTracker = new SessionEventTracker();
        var mapperOptions = new GatewayEventMapperOptions { FilterThinking = _options.FilterThinking };

        runState.Session = await _sessionResolver.EnsureSessionAsync(sessionId, request.AgentId, cancellationToken);

        var userMessage = GatewayUserMessageComposer.Compose(request.Input, request.Quote);
        if (userMessage != null)
            runState.Session.AddMessage(userMessage);

        var agentId = await _selectionResolver.ResolveAgentIdAsync(
            request.AgentId,
            runState.Session.SelectedAgent,
            cancellationToken);

        var agentInstance = agentRegistry.GetOrCreateAgentInstance(agentId)
            ?? throw new InvalidOperationException($"Agent '{agentId}' not found");

        ApplyModelSelection(agentInstance, request.ModelId, runState.Session, agentId);

        runState.Session.SelectedAgent = agentId;

        AcpExecutionOverrides? acpOverrides = null;
        if (agentInstance.Runtime == AgentRuntime.AcpPassthrough)
        {
            acpOverrides = AcpExecutionContextBuilder.Resolve(
                _selectionResolver,
                request.ModelId,
                request.ModeId,
                runState.Session);
            AcpExecutionContextBuilder.ApplyToSession(runState.Session, acpOverrides);
        }
        else
        {
            var resolvedModel = _selectionResolver.ResolveModelId(
                request.ModelId,
                runState.Session.SelectedModel,
                agentId);
            if (!string.IsNullOrEmpty(resolvedModel))
                runState.Session.SelectedModel = resolvedModel;
        }

        var history = BuildHistoryFromSession(runState.Session);

        var workingDirectory = runState.Session?.WorkingDirectory ?? workspaceRoot;

        var context = new AgentContext
        {
            SessionId = sessionId,
            CancellationToken = cancellationToken,
            PermissionChannel = _permissionChannel,
            History = history,
            WorkingDirectory = workingDirectory,
            WorkspaceRoot = workspaceRoot,
            Services = _services
        };

        if (acpOverrides != null)
            AcpExecutionContextBuilder.ApplyToContext(context, acpOverrides);

        var agentDefinition = AgentDefinition.FromAgent(agentInstance);
        var outputChannel = Channel.CreateUnbounded<GatewayEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        IGatewayEventSink sink = _connectionManager == null
            ? new ChannelGatewayEventSink(outputChannel.Writer)
            : new CompositeGatewayEventSink(new ChannelGatewayEventSink(outputChannel.Writer), _connectionManager);

        GatewayPermissionChannel.SetRunContext(new PermissionRunContext
        {
            SessionId = sessionId,
            LoopId = runState.CurrentLoopId,
            Sink = sink
        });

        var agentTask = RunAgentAsync(
            executionRouter,
            agentDefinition,
            context,
            sessionTracker,
            runState,
            sessionManager,
            sessionId,
            mapperOptions,
            outputChannel.Writer,
            cancellationToken);

        try
        {
            await foreach (var gatewayEvent in outputChannel.Reader.ReadAllAsync(cancellationToken))
                yield return gatewayEvent;
        }
        finally
        {
            GatewayPermissionChannel.SetRunContext(null);
            outputChannel.Writer.TryComplete();
            await agentTask;
        }
    }

    private async Task RunAgentAsync(
        IAgentExecutionRouter executionRouter,
        AgentDefinition agentDefinition,
        AgentContext context,
        SessionEventTracker sessionTracker,
        ChatRunState runState,
        SessionManager sessionManager,
        string sessionId,
        GatewayEventMapperOptions mapperOptions,
        ChannelWriter<GatewayEvent> outputWriter,
        CancellationToken cancellationToken)
    {
        IGatewayEventSink CreateSink() => _connectionManager == null
            ? new ChannelGatewayEventSink(outputWriter)
            : new CompositeGatewayEventSink(new ChannelGatewayEventSink(outputWriter), _connectionManager);

        try
        {
            await foreach (var coreEvent in executionRouter.ExecuteAsync(agentDefinition, context, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (coreEvent is LoopStartEvent loopStart)
                    runState.CurrentLoopId = loopStart.LoopId;

                GatewayPermissionChannel.SetRunContext(new PermissionRunContext
                {
                    SessionId = sessionId,
                    LoopId = runState.CurrentLoopId,
                    Sink = CreateSink()
                });

                if (runState.Session != null)
                    sessionTracker.ApplyEvent(runState.Session, coreEvent);

                if (GatewayEventMapper.TryMap(coreEvent, out var gatewayEvent, mapperOptions) && gatewayEvent != null)
                    await outputWriter.WriteAsync(gatewayEvent, cancellationToken);
            }

            await sessionManager.SaveAsync(sessionId);
        }
        finally
        {
            outputWriter.TryComplete();
        }
    }

    private async Task<GatewayEvent> BuildCancelledEventAsync(string sessionId, ChatRunState runState)
    {
        if (runState.Session != null)
        {
            runState.Session.AddMessage(SessionMessage.SystemMessage("⚠️ 执行已取消"));
            await _services.GetRequiredService<SessionManager>().SaveAsync(sessionId);
        }

        return new GatewayEvent
        {
            Object = GatewayEventObject.Response,
            Status = GatewayEventStatus.Cancelled,
            SessionId = sessionId,
            Data = new GatewayEventData { CancelReason = "用户取消或连接断开" }
        };
    }

    private async Task<GatewayEvent> BuildErrorEventAsync(string sessionId, ChatRunState runState, Exception ex)
    {
        if (runState.Session != null)
        {
            runState.Session.AddMessage(SessionMessage.SystemMessage($"❌ 执行出错: {ex.Message}"));
            await _services.GetRequiredService<SessionManager>().SaveAsync(sessionId);
        }

        return new GatewayEvent
        {
            Object = GatewayEventObject.Error,
            Status = GatewayEventStatus.Failed,
            SessionId = sessionId,
            Data = new GatewayEventData
            {
                Error = ex.Message,
                ErrorSource = "gateway.orchestrator"
            }
        };
    }

    private sealed class ChatRunState
    {
        public SessionData? Session { get; set; }

        public string? CurrentLoopId { get; set; }
    }

    /// <summary>停止指定会话的活跃运行</summary>
    public bool StopChat(string sessionId) => _runTracker.StopRun(sessionId);

    private void ApplyModelSelection(IAgent agentInstance, string? modelId, SessionData session, string agentName)
    {
        if (agentInstance.Runtime == AgentRuntime.AcpPassthrough)
            return;

        var useModel = _selectionResolver.ResolveModelId(modelId, session.SelectedModel, agentName);
        if (string.IsNullOrEmpty(useModel))
            return;

        var modelRef = ModelReference.Parse(useModel);
        if (modelRef != null)
        {
            if (string.IsNullOrEmpty(modelRef.ProviderId) && !string.IsNullOrEmpty(session.SelectedModelProvider))
                modelRef.ProviderId = session.SelectedModelProvider;
            agentInstance.Model = modelRef;
        }
        else
        {
            agentInstance.Model = new ModelReference
            {
                ModelId = useModel,
                ProviderId = session.SelectedModelProvider
            };
        }
    }

    private static List<ChatMessage> BuildHistoryFromSession(SessionData session)
    {
        var history = new List<ChatMessage>();

        foreach (var msg in session.Messages)
        {
            var chatMessage = new ChatMessage
            {
                Role = msg.Role,
                Content = msg.Content ?? string.Empty,
                ReasoningContent = msg.ReasoningContent
            };

            if (msg.Parts is { Count: > 0 })
            {
                chatMessage.Parts = msg.Parts.Select(p => new ChatContentPart
                {
                    Type = p.Type,
                    Text = p.Text,
                    Url = p.Url,
                    DataBase64 = p.DataBase64,
                    MimeType = p.MimeType,
                    FileName = p.FileName,
                    FileId = p.FileId,
                    ImageDetail = p.ImageDetail
                }).ToList();
            }

            if (msg.ToolCalls is { Count: > 0 })
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

    /// <summary>将 Core 事件增量写入 SessionData（简化版 EventStreamHandler）</summary>
    private sealed class SessionEventTracker
    {
        private SessionMessage? _currentAssistantMessage;
        private string? _currentLoopId;
        private int _currentStep;

        public void ApplyEvent(SessionData session, IMessageEvent evt)
        {
            switch (evt)
            {
                case LoopStartEvent loopStart:
                    _currentLoopId = loopStart.LoopId;
                    _currentAssistantMessage = null;
                    _currentStep = 0;
                    break;

                case StreamStartEvent streamStart:
                    if (!string.IsNullOrEmpty(streamStart.LoopId))
                        _currentLoopId = streamStart.LoopId;
                    if (streamStart.Step > 0 || _currentAssistantMessage == null)
                    {
                        _currentStep = streamStart.Step;
                        _currentAssistantMessage = null;
                    }
                    break;

                case StreamDeltaEvent delta:
                    EnsureAssistantMessage(session, delta.SessionId);
                    if (_currentAssistantMessage != null)
                    {
                        if (!string.IsNullOrEmpty(delta.ContentDelta))
                            _currentAssistantMessage.Content += delta.ContentDelta;
                        if (!string.IsNullOrEmpty(delta.ReasoningDelta))
                            _currentAssistantMessage.ReasoningContent =
                                (_currentAssistantMessage.ReasoningContent ?? string.Empty) + delta.ReasoningDelta;
                    }
                    break;

                case StreamCompleteEvent complete:
                    if (_currentAssistantMessage != null && complete.Message != null)
                    {
                        if (string.IsNullOrEmpty(_currentAssistantMessage.Content) && !string.IsNullOrEmpty(complete.Message.Content))
                            _currentAssistantMessage.Content = complete.Message.Content;
                        if (string.IsNullOrEmpty(_currentAssistantMessage.ReasoningContent) && !string.IsNullOrEmpty(complete.Message.ReasoningContent))
                            _currentAssistantMessage.ReasoningContent = complete.Message.ReasoningContent;
                    }
                    break;

                case ToolCallEvent toolCall:
                    EnsureAssistantMessage(session, toolCall.SessionId);
                    if (_currentAssistantMessage == null)
                        break;

                    _currentAssistantMessage.ToolCalls ??= new List<SessionToolCall>();
                    var existing = _currentAssistantMessage.ToolCalls.Find(t => t.Id == toolCall.ToolCallId);
                    if (existing == null)
                    {
                        existing = new SessionToolCall
                        {
                            Id = toolCall.ToolCallId ?? Guid.NewGuid().ToString("N"),
                            Name = toolCall.ToolName ?? string.Empty,
                            Arguments = FormatArguments(toolCall.Arguments)
                        };
                        _currentAssistantMessage.ToolCalls.Add(existing);
                    }

                    existing.Status = toolCall.Status.ToString().ToLowerInvariant();
                    if (!string.IsNullOrEmpty(toolCall.Output))
                        existing.Result = toolCall.Output;
                    if (!string.IsNullOrEmpty(toolCall.Error))
                        existing.Error = toolCall.Error;
                    break;

                case LoopCancelledEvent cancelled:
                    session.AddMessage(SessionMessage.SystemMessage($"⚠️ 对话已取消: {cancelled.Reason}"));
                    break;

                case ErrorEvent error:
                    session.AddMessage(SessionMessage.SystemMessage($"⚠️ 错误: {error.Message}"));
                    break;
            }
        }

        private void EnsureAssistantMessage(SessionData session, string sessionId)
        {
            if (_currentAssistantMessage != null)
                return;

            _currentAssistantMessage = SessionMessage.AssistantMessage(string.Empty);
            var loopPrefix = _currentLoopId ?? sessionId;
            _currentAssistantMessage.Id = $"{loopPrefix}_step{_currentStep}";
            _currentAssistantMessage.Step = _currentStep;
            _currentAssistantMessage.LoopId = _currentLoopId;
            session.AddMessage(_currentAssistantMessage);
        }

        private static string FormatArguments(object? arguments)
        {
            if (arguments == null)
                return "{}";

            if (arguments is string str)
                return str;

            return JsonSerializer.Serialize(arguments);
        }
    }
}
