using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Channels;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Gateway.Configuration;
using Seeing.Agent.Gateway.Core;
using Seeing.Gateway.Models;
using Seeing.Gateway.Protocol;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Seeing.Agent.Gateway.Endpoints;

/// <summary>
/// Gateway WebSocket 处理器：submit / cancel / permission / ping
/// </summary>
public sealed class GatewayWebSocketHandler
{
    private readonly GatewayOrchestratorV2 _orchestrator;
    private readonly IPermissionRequestManager _permissionManager;
    private readonly GatewayConnectionManager _connectionManager;
    private readonly IChannelDirectory _channelDirectory;
    private readonly GatewayOptions _options;
    private readonly ILogger<GatewayWebSocketHandler> _logger;

    public GatewayWebSocketHandler(
        GatewayOrchestratorV2 orchestrator,
        IPermissionRequestManager permissionManager,
        GatewayConnectionManager connectionManager,
        IChannelDirectory channelDirectory,
        GatewayOptions options,
        ILogger<GatewayWebSocketHandler> logger)
    {
        _orchestrator = orchestrator;
        _permissionManager = permissionManager;
        _connectionManager = connectionManager;
        _channelDirectory = channelDirectory;
        _options = options;
        _logger = logger;
    }

    public async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
        var connection = _connectionManager.Register(webSocket);

        try
        {
            await connection.SendFrameAsync(
                GatewayWsFrameSerializer.Create(
                    GatewayWsFrameType.Connected,
                    payload: new GatewayConnectedPayload()),
                context.RequestAborted);

            await ReadLoopAsync(connection, context.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("WebSocket 连接已关闭: {ConnectionId}", connection.ConnectionId);
        }
        catch (WebSocketException ex)
        {
            _logger.LogDebug(ex, "WebSocket 连接异常: {ConnectionId}", connection.ConnectionId);
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "WebSocket 连接已断开: {ConnectionId}", connection.ConnectionId);
        }
        finally
        {
            _connectionManager.Unregister(connection.ConnectionId);
            if (webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None);
                }
                catch
                {
                    // ignore close errors
                }
            }
        }
    }

    private async Task ReadLoopAsync(GatewayWsConnection connection, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];

        while (connection.WebSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;

            do
            {
                result = await connection.WebSocket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                    return;

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    await SendErrorAsync(connection, "Only text JSON frames are supported", "unsupported_frame");
                    continue;
                }

                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            var json = Encoding.UTF8.GetString(message.ToArray());
            var frame = GatewayWsFrameSerializer.Deserialize(json);
            if (frame == null)
            {
                await SendErrorAsync(connection, "Invalid JSON frame", "invalid_json");
                continue;
            }

            await DispatchFrameAsync(connection, frame, cancellationToken);
        }
    }

    private async Task DispatchFrameAsync(
        GatewayWsConnection connection,
        GatewayWsFrame frame,
        CancellationToken cancellationToken)
    {
        switch (frame.Type)
        {
            case GatewayWsFrameType.Ping:
                await connection.SendFrameAsync(
                    GatewayWsFrameSerializer.Create(GatewayWsFrameType.Pong, frame.Id),
                    cancellationToken);
                break;

            case GatewayWsFrameType.Submit:
                _ = HandleSubmitAsync(connection, frame, cancellationToken);
                break;

            case GatewayWsFrameType.Cancel:
                await HandleCancelAsync(connection, frame, cancellationToken);
                break;

            case GatewayWsFrameType.PermissionRespond:
                await HandlePermissionRespondAsync(connection, frame, cancellationToken);
                break;

            case GatewayWsFrameType.ChannelRegister:
                await HandleChannelRegisterAsync(connection, frame);
                break;

            default:
                await SendErrorAsync(connection, $"Unsupported frame type: {frame.Type}", "unsupported_type");
                break;
        }
    }

    private async Task HandleChannelRegisterAsync(GatewayWsConnection connection, GatewayWsFrame frame)
    {
        if (frame.Payload == null)
        {
            await SendErrorAsync(connection, "channelRegister frame requires payload", "invalid_payload");
            return;
        }

        GatewayChannelRegisterPayload? payload;
        try
        {
            payload = frame.Payload.Value.Deserialize<GatewayChannelRegisterPayload>(
                GatewayWsFrameSerializer.JsonOptions);
        }
        catch (JsonException ex)
        {
            // 能力缺失/结构非法：回错但不注册、不断连
            await SendErrorAsync(connection, $"invalid channel capabilities: {ex.Message}", "invalid_capabilities");
            return;
        }

        if (payload == null || string.IsNullOrWhiteSpace(payload.Channel))
        {
            await SendErrorAsync(connection, "channel is required", "invalid_payload");
            return;
        }

        if (payload.Capabilities is null)
        {
            await SendErrorAsync(connection, "capabilities is required", "invalid_capabilities");
            return;
        }

        _connectionManager.RegisterChannel(connection.ConnectionId, payload.Channel, payload.Capabilities);
        _logger.LogInformation(
            "Channel registered: {Channel} connection={ConnectionId}",
            payload.Channel,
            connection.ConnectionId);
    }

    private async Task HandleSubmitAsync(
        GatewayWsConnection connection,
        GatewayWsFrame frame,
        CancellationToken cancellationToken)
    {
        GatewayRequest? request = null;
        try
        {
            if (frame.Payload == null)
                throw new InvalidOperationException("submit frame requires payload");

            request = frame.Payload.Value.Deserialize<GatewayRequest>(GatewayWsFrameSerializer.JsonOptions)
                ?? throw new InvalidOperationException("submit payload is empty");

            // ① 先注册请求/应答实时订阅（不回放）：注册点先于投递唤醒，整轮事件自 LoopStart 起不漏。
            //    桥侧 GatewayAssistantReplyCollector 轮次门控会忽略上一轮仍在途的尾流。
            using var replyStream = _orchestrator.BeginReplyStream(request.SessionId);

            // ② 投递并唤醒消费循环
            var submit = await _orchestrator.SubmitAsync(request, cancellationToken).ConfigureAwait(false);

            await connection.SendFrameAsync(
                GatewayWsFrameSerializer.Create(
                    GatewayWsFrameType.SubmitAck,
                    frame.Id,
                    new GatewaySubmitAckPayload
                    {
                        SessionId = submit.SessionId,
                        // 回填 submit 帧关联 ID：客户端请求/应答订阅按此过滤（frame.Id 与之同值）
                        RequestId = frame.Id,
                        Success = submit.Success,
                        Error = submit.Error,
                        PendingInputCount = submit.PendingInputCount
                    }),
                cancellationToken).ConfigureAwait(false);

            if (!submit.Success)
                return;

            RegisterInbound(request);
            _connectionManager.SubscribeSession(connection.ConnectionId, request.SessionId);

            // ③ 枚举实时流：事件已在实时订阅通道缓冲，投递前后到达均不丢
            string? loopId = null;
            await foreach (var gatewayEvent in replyStream.StreamAsync(cancellationToken))
            {
                loopId = gatewayEvent.LoopId ?? loopId;
                await connection.SendFrameAsync(
                    GatewayWsFrameSerializer.Create(GatewayWsFrameType.ChatEvent, frame.Id, gatewayEvent),
                    cancellationToken);
            }

            await _connectionManager.SendExecutionCompleteAsync(
                connection.ConnectionId,
                frame.Id,
                new GatewayExecutionCompletePayload
                {
                    SessionId = request.SessionId,
                    LoopId = loopId
                },
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // client disconnected
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WebSocket submit failed: SessionId={SessionId}", request?.SessionId);
            await connection.SendFrameAsync(
                GatewayWsFrameSerializer.Create(
                    GatewayWsFrameType.ChatError,
                    frame.Id,
                    new GatewayWsErrorPayload { Message = ex.Message, Code = "submit_failed" }),
                cancellationToken);
        }
    }

    private async Task HandleCancelAsync(
        GatewayWsConnection connection,
        GatewayWsFrame frame,
        CancellationToken cancellationToken)
    {
        if (frame.Payload == null)
        {
            await SendErrorAsync(connection, "cancel frame requires payload", "invalid_payload");
            return;
        }

        var payload = frame.Payload.Value.Deserialize<GatewayCancelPayload>(GatewayWsFrameSerializer.JsonOptions);
        if (payload == null || string.IsNullOrWhiteSpace(payload.SessionId))
        {
            await SendErrorAsync(connection, "sessionId is required", "invalid_payload");
            return;
        }

        var mode = string.IsNullOrWhiteSpace(payload.Mode) ? "interrupt" : payload.Mode;
        var cancelled = await _orchestrator.CancelAsync(payload.SessionId, mode, cancellationToken).ConfigureAwait(false);
        await connection.SendFrameAsync(
            GatewayWsFrameSerializer.Create(
                GatewayWsFrameType.CancelAck,
                frame.Id,
                new GatewayCancelAckPayload { SessionId = payload.SessionId, Cancelled = cancelled }),
            cancellationToken);
    }

    private async Task HandlePermissionRespondAsync(
        GatewayWsConnection connection,
        GatewayWsFrame frame,
        CancellationToken cancellationToken)
    {
        if (frame.Payload == null)
        {
            await SendErrorAsync(connection, "permission.respond frame requires payload", "invalid_payload");
            return;
        }

        var payload = frame.Payload.Value.Deserialize<GatewayPermissionRespondPayload>(GatewayWsFrameSerializer.JsonOptions);
        if (payload == null || string.IsNullOrWhiteSpace(payload.SessionId) || string.IsNullOrWhiteSpace(payload.PermissionId))
        {
            await SendErrorAsync(connection, "sessionId and permissionId are required", "invalid_payload");
            return;
        }

        var resolved = _permissionManager.TryResolve(
            payload.PermissionId,
            payload.Allow ? PermissionEffect.Allow : PermissionEffect.Deny,
            PermissionGrantScope.Once,
            PermissionResolvedBy.User,
            payload.Reason,
            expectedSessionId: payload.SessionId);

        var result = resolved
            ? GatewayPermissionRespondResult.Ok()
            : GatewayPermissionRespondResult.Fail("权限请求不存在、已过期或会话不匹配");

        await connection.SendFrameAsync(
            GatewayWsFrameSerializer.Create(GatewayWsFrameType.PermissionAck, frame.Id, result),
            cancellationToken);
    }

    private void RegisterInbound(GatewayRequest request)
    {
        if (request.Sender is null || request.Conversation is null)
            return;

        var text = request.Input is null
            ? string.Empty
            : string.Join("\n", request.Input.OfType<GatewayTextContentPart>().Select(part => part.Text));

        _channelDirectory.RegisterInbound(new ChannelInboundMessage
        {
            Sender = request.Sender,
            Conversation = request.Conversation,
            ReplyToMsgId = request.ReplyToMsgId,
            SessionId = request.SessionId,
            Text = text,
            CommandToken = null
        });
    }

    private Task SendErrorAsync(GatewayWsConnection connection, string message, string code) =>
        connection.SendFrameAsync(
            GatewayWsFrameSerializer.Create(
                GatewayWsFrameType.Error,
                payload: new GatewayWsErrorPayload { Message = message, Code = code }));
}
