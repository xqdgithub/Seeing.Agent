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
/// Gateway WebSocket 处理器：submit / cancel / permission / ping
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

            case GatewayWsFrameType.Submit:
                _ = HandleSubmitAsync(connection, frame, cancellationToken);
                break;

            case GatewayWsFrameType.Cancel:
                await HandleCancelAsync(connection, frame, cancellationToken);
                break;

            case GatewayWsFrameType.PermissionRespond:
                await HandlePermissionRespondAsync(connection, frame, cancellationToken);
                break;

            default:
                await SendErrorAsync(connection, $"Unsupported frame type: {frame.Type}", "unsupported_type");
                break;
        }
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

            var submit = await _orchestrator.SubmitAsync(request, cancellationToken).ConfigureAwait(false);

            await connection.SendFrameAsync(
                GatewayWsFrameSerializer.Create(
                    GatewayWsFrameType.SubmitAck,
                    frame.Id,
                    new GatewaySubmitAckPayload
                    {
                        SessionId = submit.SessionId,
                        ExecutionId = submit.ExecutionId,
                        Success = submit.Success,
                        Error = submit.Error,
                        QueuePosition = submit.QueuePosition
                    }),
                cancellationToken).ConfigureAwait(false);

            if (!submit.Success || string.IsNullOrEmpty(submit.ExecutionId))
                return;

            _connectionManager.SubscribeSession(connection.ConnectionId, request.SessionId);

            string? loopId = null;
            await foreach (var gatewayEvent in _orchestrator.SubscribeExecutionEventsAsync(
                               request.SessionId, submit.ExecutionId, cancellationToken))
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
                    ExecutionId = submit.ExecutionId,
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
        if (payload == null || string.IsNullOrWhiteSpace(payload.ExecutionId))
        {
            await SendErrorAsync(connection, "executionId is required", "invalid_payload");
            return;
        }

        var cancelled = _orchestrator.Cancel(payload.ExecutionId);
        await connection.SendFrameAsync(
            GatewayWsFrameSerializer.Create(
                GatewayWsFrameType.CancelAck,
                frame.Id,
                new GatewayCancelAckPayload { ExecutionId = payload.ExecutionId, Cancelled = cancelled }),
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
