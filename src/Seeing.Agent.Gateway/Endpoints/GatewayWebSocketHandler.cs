using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Configuration;
using Seeing.Agent.Gateway.Core;
using Seeing.Agent.Gateway.Permission;
using Seeing.Gateway.Models;
using Seeing.Gateway.Protocol;

namespace Seeing.Agent.Gateway.Endpoints;

/// <summary>
/// Gateway WebSocket 处理器：chat / stop / permission / ping
/// </summary>
public sealed class GatewayWebSocketHandler
{
    private readonly GatewayOrchestratorV2 _orchestrator;
    private readonly GatewayPermissionChannel _permissionChannel;
    private readonly GatewayConnectionManager _connectionManager;
    private readonly GatewayOptions _options;
    private readonly ILogger<GatewayWebSocketHandler> _logger;

    public GatewayWebSocketHandler(
        GatewayOrchestratorV2 orchestrator,
        GatewayPermissionChannel permissionChannel,
        GatewayConnectionManager connectionManager,
        GatewayOptions options,
        ILogger<GatewayWebSocketHandler> logger)
    {
        _orchestrator = orchestrator;
        _permissionChannel = permissionChannel;
        _connectionManager = connectionManager;
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

            case GatewayWsFrameType.Chat:
                _ = HandleChatAsync(connection, frame, cancellationToken);
                break;

            case GatewayWsFrameType.Stop:
                await HandleStopAsync(connection, frame, cancellationToken);
                break;

            case GatewayWsFrameType.PermissionRespond:
                await HandlePermissionRespondAsync(connection, frame, cancellationToken);
                break;

            default:
                await SendErrorAsync(connection, $"Unsupported frame type: {frame.Type}", "unsupported_type");
                break;
        }
    }

    private async Task HandleChatAsync(
        GatewayWsConnection connection,
        GatewayWsFrame frame,
        CancellationToken cancellationToken)
    {
        GatewayRequest? request = null;
        try
        {
            if (frame.Payload == null)
                throw new InvalidOperationException("chat frame requires payload");

            request = frame.Payload.Value.Deserialize<GatewayRequest>(GatewayWsFrameSerializer.JsonOptions)
                ?? throw new InvalidOperationException("chat payload is empty");

            _connectionManager.SubscribeSession(connection.ConnectionId, request.SessionId);

            string? loopId = null;
            await foreach (var gatewayEvent in _orchestrator.ExecuteChatAsync(request, cancellationToken))
            {
                loopId = gatewayEvent.LoopId ?? loopId;
                await connection.SendFrameAsync(
                    GatewayWsFrameSerializer.Create(GatewayWsFrameType.ChatEvent, frame.Id, gatewayEvent),
                    cancellationToken);
            }

            await _connectionManager.SendChatCompleteAsync(
                connection.ConnectionId,
                frame.Id,
                new GatewayChatCompletePayload
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
            _logger.LogError(ex, "WebSocket chat failed: SessionId={SessionId}", request?.SessionId);
            await connection.SendFrameAsync(
                GatewayWsFrameSerializer.Create(
                    GatewayWsFrameType.ChatError,
                    frame.Id,
                    new GatewayWsErrorPayload { Message = ex.Message, Code = "chat_failed" }),
                cancellationToken);
        }
    }

    private async Task HandleStopAsync(
        GatewayWsConnection connection,
        GatewayWsFrame frame,
        CancellationToken cancellationToken)
    {
        if (frame.Payload == null)
        {
            await SendErrorAsync(connection, "stop frame requires payload", "invalid_payload");
            return;
        }

        var payload = frame.Payload.Value.Deserialize<GatewayStopPayload>(GatewayWsFrameSerializer.JsonOptions);
        if (payload == null || string.IsNullOrWhiteSpace(payload.SessionId))
        {
            await SendErrorAsync(connection, "sessionId is required", "invalid_payload");
            return;
        }

        var stopped = _orchestrator.StopChat(payload.SessionId);
        await connection.SendFrameAsync(
            GatewayWsFrameSerializer.Create(
                GatewayWsFrameType.StopAck,
                frame.Id,
                new GatewayStopAckPayload { SessionId = payload.SessionId, Stopped = stopped }),
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

        var result = _permissionChannel.Respond(payload.SessionId, payload.PermissionId, payload.Allow, payload.Reason);
        await connection.SendFrameAsync(
            GatewayWsFrameSerializer.Create(GatewayWsFrameType.PermissionAck, frame.Id, result),
            cancellationToken);
    }

    private Task SendErrorAsync(GatewayWsConnection connection, string message, string code) =>
        connection.SendFrameAsync(
            GatewayWsFrameSerializer.Create(
                GatewayWsFrameType.Error,
                payload: new GatewayWsErrorPayload { Message = message, Code = code }));
}
