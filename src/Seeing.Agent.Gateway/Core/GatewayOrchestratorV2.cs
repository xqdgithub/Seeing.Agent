using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seeing.Agent.App;
using Seeing.Agent.App.Events;
using Seeing.Agent.App.Execution;
using Seeing.Agent.App.Models;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Events;
using Seeing.Agent.Gateway.Permission;
using Seeing.Gateway.Models;
using Seeing.Session.Core;
using Seeing.Session.Management;

namespace Seeing.Agent.Gateway.Core;

/// <summary>
/// Gateway 聊天编排器 — Submit / Subscribe / Cancel，对齐 IChatOrchestrator。
/// </summary>
public sealed class GatewayOrchestratorV2
{
    private readonly IServiceProvider _services;
    private readonly GatewayOptions _options;
    private readonly GatewayPermissionChannel _permissionChannel;
    private readonly GatewayRunTracker _runTracker;
    private readonly SessionExecutionQueue _executionQueue;
    private readonly GatewayConnectionManager? _connectionManager;
    private readonly ILogger<GatewayOrchestratorV2> _logger;

    public GatewayOrchestratorV2(
        IServiceProvider services,
        GatewayOptions options,
        GatewayPermissionChannel permissionChannel,
        GatewayRunTracker runTracker,
        SessionExecutionQueue executionQueue,
        ILogger<GatewayOrchestratorV2> logger,
        GatewayConnectionManager? connectionManager = null)
    {
        _services = services;
        _options = options;
        _permissionChannel = permissionChannel;
        _runTracker = runTracker;
        _executionQueue = executionQueue;
        _connectionManager = connectionManager;
        _logger = logger;
    }

    /// <summary>提交执行（非阻塞）</summary>
    public async Task<GatewaySubmitResult> SubmitAsync(
        GatewayRequest request,
        CancellationToken cancellationToken = default)
    {
        var sessionId = request.SessionId;
        var channelId = request.ChannelId ?? "default";

        await _executionQueue.WaitAsync(channelId, sessionId, cancellationToken).ConfigureAwait(false);
        try
        {
            using var scope = _services.CreateScope();
            var chatOrchestrator = scope.ServiceProvider.GetRequiredService<IChatOrchestrator>();
            var input = BuildChatInput(request);
            var options = BuildChatOptions(request);

            var result = await chatOrchestrator.SubmitAsync(sessionId, input, options).ConfigureAwait(false);
            if (!result.Success || string.IsNullOrEmpty(result.ExecutionId))
            {
                return GatewaySubmitResult.Failed(sessionId, result.Error ?? "Submit failed");
            }

            return GatewaySubmitResult.Succeeded(sessionId, result.ExecutionId, result.QueuePosition);
        }
        finally
        {
            _executionQueue.Release(channelId, sessionId);
        }
    }

    /// <summary>订阅指定 execution 的 Gateway 事件，直到匹配的 ExecutionComplete。</summary>
    public async IAsyncEnumerable<GatewayEvent> SubscribeExecutionEventsAsync(
        string sessionId,
        string executionId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var runCts = _runTracker.RegisterRun(executionId, sessionId);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, runCts.Token);

        var runState = new ChatRunState();
        Exception? fault = null;
        var cancelled = false;

        await using var enumerator = StreamExecutionEventsAsync(sessionId, executionId, linkedCts.Token, runState)
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

            yield return enumerator.Current with { ExecutionId = executionId };
        }

        _runTracker.UnregisterRun(executionId);

        if (cancelled)
            yield return await BuildCancelledEventAsync(sessionId, runState) with { ExecutionId = executionId };
        else if (fault != null)
        {
            _logger.LogError(fault, "Gateway 执行订阅失败: SessionId={SessionId}, ExecutionId={ExecutionId}",
                sessionId, executionId);
            yield return await BuildErrorEventAsync(sessionId, runState, fault) with { ExecutionId = executionId };
        }
    }

    /// <summary>取消指定执行</summary>
    public bool Cancel(string executionId)
    {
        using var scope = _services.CreateScope();
        var chatOrchestrator = scope.ServiceProvider.GetRequiredService<IChatOrchestrator>();
        var cancelled = chatOrchestrator.Cancel(executionId);
        _runTracker.CancelRun(executionId);
        return cancelled;
    }

    private async IAsyncEnumerable<GatewayEvent> StreamExecutionEventsAsync(
        string sessionId,
        string executionId,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        ChatRunState runState)
    {
        var sessionManager = _services.GetRequiredService<SessionManager>();

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

        var executionTask = MapSubscriptionAsync(
            sessionId,
            executionId,
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

    private async Task MapSubscriptionAsync(
        string sessionId,
        string executionId,
        ChatRunState runState,
        SessionManager sessionManager,
        ChannelWriter<GatewayEvent> outputWriter,
        CancellationToken cancellationToken)
    {
        using var scope = _services.CreateScope();
        var chatOrchestrator = scope.ServiceProvider.GetRequiredService<IChatOrchestrator>();

        try
        {
            await foreach (var chatEvent in chatOrchestrator.SubscribeEvents(sessionId, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (chatEvent is ExecutionCompleteEvent complete
                    && !string.Equals(complete.ExecutionId, executionId, StringComparison.Ordinal))
                {
                    continue;
                }

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

                if (chatEvent is SessionUpdatedEvent sessionEvt)
                    runState.Session = sessionEvt.Session;

                if (ChatEventMapper.TryMap(chatEvent, out var gatewayEvent) && gatewayEvent != null)
                {
                    await outputWriter.WriteAsync(
                        gatewayEvent with { ExecutionId = executionId },
                        cancellationToken);
                }

                if (chatEvent is ExecutionCompleteEvent done
                    && string.Equals(done.ExecutionId, executionId, StringComparison.Ordinal))
                {
                    break;
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

        if (request.Input != null && request.Input.Count > 0)
        {
            var textParts = request.Input
                .OfType<GatewayTextContentPart>()
                .Select(p => p.Text)
                .ToList();

            if (textParts.Count > 0)
                text = string.Join("\n", textParts);

            attachments = new List<ChatAttachment>();
            foreach (var part in request.Input)
            {
                if (part is GatewayImageContentPart img)
                {
                    attachments.Add(new ChatAttachment
                    {
                        Base64Data = img.Url,
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
                attachments = null;
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
            WorkingDirectory = null
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

    private sealed class ChatRunState
    {
        public SessionData? Session { get; set; }
        public string? CurrentLoopId { get; set; }
    }
}
