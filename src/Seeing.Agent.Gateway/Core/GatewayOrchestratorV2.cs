using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seeing.Agent.App;
using Seeing.Agent.App.Events;
using Seeing.Agent.App.Models;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Events;
using Seeing.Agent.Gateway.Permission;
using Seeing.Gateway.Models;
using Seeing.Session.Core;
using Seeing.Session.Management;

namespace Seeing.Agent.Gateway.Core;

/// <summary>
/// Gateway 聊天编排器 - 使用 IChatOrchestrator 作为核心执行引擎
/// <para>
/// 职责：
/// - 管理 Gateway 特有的执行队列和取消机制
/// - 将 ChatEvent 映射为 GatewayEvent
/// - 处理权限通道上下文
/// - 跟踪会话事件以更新 SessionData
/// </para>
/// </summary>
public sealed class GatewayOrchestratorV2
{
    private readonly IServiceProvider _services;
    private readonly IChatOrchestrator _chatOrchestrator;
    private readonly GatewayOptions _options;
    private readonly GatewayPermissionChannel _permissionChannel;
    private readonly GatewayRunTracker _runTracker;
    private readonly SessionExecutionQueue _executionQueue;
    private readonly GatewayConnectionManager? _connectionManager;
    private readonly ILogger<GatewayOrchestratorV2> _logger;

    public GatewayOrchestratorV2(
        IServiceProvider services,
        IChatOrchestrator chatOrchestrator,
        GatewayOptions options,
        GatewayPermissionChannel permissionChannel,
        GatewayRunTracker runTracker,
        SessionExecutionQueue executionQueue,
        ILogger<GatewayOrchestratorV2> logger,
        GatewayConnectionManager? connectionManager = null)
    {
        _services = services;
        _chatOrchestrator = chatOrchestrator;
        _options = options;
        _permissionChannel = permissionChannel;
        _runTracker = runTracker;
        _executionQueue = executionQueue;
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
        var sessionTracker = new SessionEventTracker();

        // 设置权限通道上下文
        var outputChannel = Channel.CreateUnbounded<GatewayEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        IGatewayEventSink sink = _connectionManager == null
            ? new ChannelGatewayEventSink(outputChannel.Writer)
            : new CompositeGatewayEventSink(
                new ChannelGatewayEventSink(outputChannel.Writer),
                _connectionManager);

        GatewayPermissionChannel.SetRunContext(new PermissionRunContext
        {
            SessionId = sessionId,
            LoopId = null,
            Sink = sink
        });

        // 构建输入
        var input = BuildChatInput(request);
        var options = BuildChatOptions(request);

        // 执行并映射事件
        var executionTask = ExecuteAndMapAsync(
            sessionId,
            input,
            options,
            sessionTracker,
            runState,
            sessionManager,
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
            await executionTask;
        }
    }

    private async Task ExecuteAndMapAsync(
        string sessionId,
        ChatInput input,
        ChatOptions options,
        SessionEventTracker sessionTracker,
        ChatRunState runState,
        SessionManager sessionManager,
        ChannelWriter<GatewayEvent> outputWriter,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var chatEvent in _chatOrchestrator.ExecuteAsync(sessionId, input, options, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();

                // 更新权限上下文中的 LoopId
                if (chatEvent.LoopId != null)
                {
                    runState.CurrentLoopId = chatEvent.LoopId;
                    GatewayPermissionChannel.SetRunContext(new PermissionRunContext
                    {
                        SessionId = sessionId,
                        LoopId = chatEvent.LoopId,
                        Sink = CreateSink(outputWriter)
                    });
                }

                // 更新 Session 数据
                if (chatEvent is SessionUpdatedEvent sessionEvt)
                {
                    runState.Session = sessionEvt.Session;
                }

                // 映射为 Gateway 事件
                if (ChatEventMapper.TryMap(chatEvent, out var gatewayEvent) && gatewayEvent != null)
                {
                    await outputWriter.WriteAsync(gatewayEvent, cancellationToken);
                }
            }

            await sessionManager.SaveAsync(sessionId);
        }
        finally
        {
            outputWriter.TryComplete();
        }
    }

    private IGatewayEventSink CreateSink(ChannelWriter<GatewayEvent> outputWriter)
    {
        return _connectionManager == null
            ? new ChannelGatewayEventSink(outputWriter)
            : new CompositeGatewayEventSink(
                new ChannelGatewayEventSink(outputWriter),
                _connectionManager);
    }

    private static ChatInput BuildChatInput(GatewayRequest request)
    {
        string? text = null;
        List<ChatAttachment>? attachments = null;

        // 从 GatewayContentPart 提取文本
        if (request.Input != null && request.Input.Count > 0)
        {
            var textParts = request.Input
                .OfType<GatewayTextContentPart>()
                .Select(p => p.Text)
                .ToList();
            
            if (textParts.Count > 0)
            {
                text = string.Join("\n", textParts);
            }

            // 处理附件
            attachments = new List<ChatAttachment>();
            foreach (var part in request.Input)
            {
                if (part is GatewayImageContentPart img)
                {
                    attachments.Add(new ChatAttachment
                    {
                        Base64Data = img.Url, // URL or base64
                        MimeType = img.MimeType ?? "image/png",
                        FileName = ""
                    });
                }
                else if (part is GatewayFileContentPart file)
                {
                    attachments.Add(new ChatAttachment
                    {
                        Base64Data = file.Url,
                        MimeType = file.MimeType ?? "application/octet-stream",
                        FileName = file.Name ?? ""
                    });
                }
            }
            
            if (attachments.Count == 0)
            {
                attachments = null;
            }
        }

        return new ChatInput
        {
            Text = text,
            Attachments = attachments
        };
    }

    private ChatOptions BuildChatOptions(GatewayRequest request)
    {
        return new ChatOptions
        {
            AgentId = request.AgentId,
            ModelId = request.ModelId,
            ModeId = request.ModeId,
            ChannelId = request.ChannelId,
            UserId = request.UserId,
            WorkingDirectory = null // Will be resolved by orchestrator
        };
    }

    private async Task<GatewayEvent> BuildCancelledEventAsync(string sessionId, ChatRunState runState)
    {
        if (runState.Session != null)
        {
            runState.Session.Messages.Add(SessionMessage.SystemMessage("⚠️ 执行已取消"));
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
            runState.Session.Messages.Add(SessionMessage.SystemMessage($"❌ 执行出错: {ex.Message}"));
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

    /// <summary>停止指定会话的活跃运行</summary>
    public bool StopChat(string sessionId) => _runTracker.StopRun(sessionId);

    private sealed class ChatRunState
    {
        public SessionData? Session { get; set; }
        public string? CurrentLoopId { get; set; }
    }

    /// <summary>将 Core 事件增量写入 SessionData</summary>
    private sealed class SessionEventTracker
    {
        private SessionMessage? _currentAssistantMessage;
        private string? _currentLoopId;
        private int _currentStep;

        public void ApplyEvent(SessionData session, IMessageEvent evt)
        {
            switch (evt)
            {
                case LoopStartEvent:
                    _currentLoopId = evt.LoopId;
                    _currentAssistantMessage = null;
                    _currentStep = 0;
                    break;

                case StreamStartEvent startEvt:
                    if (!string.IsNullOrEmpty(evt.LoopId))
                        _currentLoopId = evt.LoopId;
                    var step = startEvt.Step;
                    if (step > 0 || _currentAssistantMessage == null)
                    {
                        _currentStep = step;
                        _currentAssistantMessage = null;
                    }
                    break;

                case StreamDeltaEvent deltaEvt:
                    EnsureAssistantMessage(session, evt.SessionId);
                    if (_currentAssistantMessage != null)
                    {
                        if (!string.IsNullOrEmpty(deltaEvt.ContentDelta))
                            _currentAssistantMessage.Content += deltaEvt.ContentDelta;
                        if (!string.IsNullOrEmpty(deltaEvt.ReasoningDelta))
                            _currentAssistantMessage.ReasoningContent =
                                (_currentAssistantMessage.ReasoningContent ?? string.Empty) + deltaEvt.ReasoningDelta;
                    }
                    break;

                case StreamCompleteEvent completeEvt:
                    if (_currentAssistantMessage != null && completeEvt.Message != null)
                    {
                        if (string.IsNullOrEmpty(_currentAssistantMessage.Content) && !string.IsNullOrEmpty(completeEvt.Message.Content))
                            _currentAssistantMessage.Content = completeEvt.Message.Content;
                        if (string.IsNullOrEmpty(_currentAssistantMessage.ReasoningContent) && !string.IsNullOrEmpty(completeEvt.Message.ReasoningContent))
                            _currentAssistantMessage.ReasoningContent = completeEvt.Message.ReasoningContent;
                    }
                    break;

                case ToolCallEvent toolEvt:
                    EnsureAssistantMessage(session, evt.SessionId);
                    if (_currentAssistantMessage == null)
                        break;

                    _currentAssistantMessage.ToolCalls ??= new List<SessionToolCall>();
                    
                    var toolCallId = toolEvt.ToolCallId ?? Guid.NewGuid().ToString("N");
                    var existing = _currentAssistantMessage.ToolCalls.Find(t => t.Id == toolCallId);
                    if (existing == null)
                    {
                        existing = new SessionToolCall
                        {
                            Id = toolCallId,
                            Name = toolEvt.ToolName ?? string.Empty,
                            Arguments = FormatArguments(toolEvt.Arguments)
                        };
                        _currentAssistantMessage.ToolCalls.Add(existing);
                    }

                    existing.Status = toolEvt.Status.ToString().ToLowerInvariant();
                    if (toolEvt.Output != null)
                        existing.Result = toolEvt.Output;
                    if (toolEvt.Error != null)
                        existing.Error = toolEvt.Error;
                    break;

                case ErrorEvent errorEvt:
                    session.Messages.Add(SessionMessage.SystemMessage(string.Format("错误: {0}", errorEvt.Message)));
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
            session.Messages.Add(_currentAssistantMessage);
        }

        private static string FormatArguments(object? arguments)
        {
            if (arguments == null)
                return "{}";

            if (arguments is string str)
                return str;

            return System.Text.Json.JsonSerializer.Serialize(arguments);
        }
    }
}
