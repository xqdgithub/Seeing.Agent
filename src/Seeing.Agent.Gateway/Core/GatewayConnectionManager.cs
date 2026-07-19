using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using Seeing.Gateway.Models;
using Seeing.Gateway.Protocol;

namespace Seeing.Agent.Gateway.Core;

/// <summary>
/// WebSocket 连接注册与 session 事件推送
/// </summary>
public sealed class GatewayConnectionManager
{
    private readonly ConcurrentDictionary<string, GatewayWsConnection> _connections = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _sessionSubscriptions = new();

    /// <summary>注册新连接</summary>
    public GatewayWsConnection Register(WebSocket webSocket)
    {
        var connection = new GatewayWsConnection(Guid.NewGuid().ToString("N"), webSocket);
        _connections[connection.ConnectionId] = connection;
        return connection;
    }

    /// <summary>移除连接</summary>
    public void Unregister(string connectionId)
    {
        if (!_connections.TryRemove(connectionId, out _))
            return;

        foreach (var (sessionId, subscribers) in _sessionSubscriptions)
        {
            subscribers.TryRemove(connectionId, out _);
            if (subscribers.IsEmpty)
                _sessionSubscriptions.TryRemove(sessionId, out _);
        }
    }

    /// <summary>订阅 session 事件推送</summary>
    public void SubscribeSession(string connectionId, string sessionId)
    {
        var subscribers = _sessionSubscriptions.GetOrAdd(sessionId, _ => new ConcurrentDictionary<string, byte>());
        subscribers[connectionId] = 0;
    }

    /// <summary>向订阅了 session 的所有连接推送 GatewayEvent</summary>
    public void PushEvent(string sessionId, GatewayEvent gatewayEvent)
    {
        if (!_sessionSubscriptions.TryGetValue(sessionId, out var subscribers))
            return;

        var frame = GatewayWsFrameSerializer.Create(
            GatewayWsFrameType.ChatEvent,
            payload: gatewayEvent);

        foreach (var connectionId in subscribers.Keys)
        {
            if (_connections.TryGetValue(connectionId, out var connection))
                _ = connection.SendFrameAsync(frame);
        }
    }

    /// <summary>向指定连接发送 execution.complete</summary>
    public Task SendExecutionCompleteAsync(
        string connectionId,
        string? requestId,
        GatewayExecutionCompletePayload payload,
        CancellationToken cancellationToken = default)
    {
        if (!_connections.TryGetValue(connectionId, out var connection))
            return Task.CompletedTask;

        var frame = GatewayWsFrameSerializer.Create(GatewayWsFrameType.ExecutionComplete, requestId, payload);
        return connection.SendFrameAsync(frame, cancellationToken);
    }

    /// <summary>向指定连接发送帧</summary>
    public Task SendFrameAsync(
        string connectionId,
        GatewayWsFrame frame,
        CancellationToken cancellationToken = default)
    {
        if (!_connections.TryGetValue(connectionId, out var connection))
            return Task.CompletedTask;

        return connection.SendFrameAsync(frame, cancellationToken);
    }
}

/// <summary>
/// 单个 WebSocket 连接封装
/// </summary>
public sealed class GatewayWsConnection(string connectionId, WebSocket webSocket)
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public string ConnectionId { get; } = connectionId;

    public WebSocket WebSocket { get; } = webSocket;

    public async Task SendFrameAsync(GatewayWsFrame frame, CancellationToken cancellationToken = default)
    {
        if (WebSocket.State != WebSocketState.Open)
            return;

        var json = GatewayWsFrameSerializer.Serialize(frame);
        var bytes = Encoding.UTF8.GetBytes(json);

        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            if (WebSocket.State == WebSocketState.Open)
            {
                await WebSocket.SendAsync(
                    bytes,
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    cancellationToken);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }
}
