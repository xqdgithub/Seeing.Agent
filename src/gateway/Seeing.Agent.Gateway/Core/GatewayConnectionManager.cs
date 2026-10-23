using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Channels;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Gateway.Models;
using Seeing.Gateway.Protocol;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;

namespace Seeing.Agent.Gateway.Core;

/// <summary>
/// WebSocket 连接注册、session 事件推送、Channel 出站路由
/// </summary>
public sealed class GatewayConnectionManager
{
    private readonly IPermissionRequestManager? _permissionManager;
    private readonly IChannelAdapterRegistry? _adapterRegistry;
    private readonly ILogger<GatewayConnectionManager> _logger;
    private readonly ConcurrentDictionary<string, GatewayWsConnection> _connections = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _sessionSubscriptions = new();
    private readonly ConcurrentDictionary<string, string> _channelOwners =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _connectionChannels = new();

    public GatewayConnectionManager(
        IPermissionRequestManager? permissionManager = null,
        IChannelAdapterRegistry? adapterRegistry = null,
        ILogger<GatewayConnectionManager>? logger = null)
    {
        _permissionManager = permissionManager;
        _adapterRegistry = adapterRegistry;
        _logger = logger ?? NullLogger<GatewayConnectionManager>.Instance;
    }

    /// <summary>注册新连接</summary>
    public GatewayWsConnection Register(WebSocket webSocket)
    {
        var connection = new GatewayWsConnection(Guid.NewGuid().ToString("N"), webSocket);
        _connections[connection.ConnectionId] = connection;
        return connection;
    }

    /// <summary>移除连接；对连接订阅过的会话取消其在途权限请求（Deny(Cancellation)）。</summary>
    public void Unregister(string connectionId)
    {
        if (!_connections.TryRemove(connectionId, out _))
            return;

        List<string>? subscribedSessions = null;
        foreach (var (sessionId, subscribers) in _sessionSubscriptions)
        {
            if (subscribers.TryRemove(connectionId, out _))
                (subscribedSessions ??= []).Add(sessionId);

            if (subscribers.IsEmpty)
                _sessionSubscriptions.TryRemove(sessionId, out _);
        }

        ReleaseChannelOwnership(connectionId);

        if (subscribedSessions is null || _permissionManager is null)
            return;

        foreach (var sessionId in subscribedSessions)
            CancelPendingForSession(sessionId);
    }

    private void CancelPendingForSession(string sessionId)
    {
        foreach (var request in _permissionManager!.GetPending(sessionId))
        {
            if (string.IsNullOrEmpty(request.RequestId))
                continue;

            _permissionManager.TryResolve(
                request.RequestId,
                PermissionEffect.Deny,
                PermissionGrantScope.Once,
                PermissionResolvedBy.Cancellation,
                "连接断开",
                expectedSessionId: sessionId);
        }
    }

    /// <summary>获取当前仍有注册连接的 channel ID</summary>
    public IReadOnlyCollection<string> GetRegisteredChannels()
        => _channelOwners.Keys.ToArray();

    /// <summary>订阅 session 事件推送</summary>
    public void SubscribeSession(string connectionId, string sessionId)
    {
        var subscribers = _sessionSubscriptions.GetOrAdd(sessionId, _ => new ConcurrentDictionary<string, byte>());
        subscribers[connectionId] = 0;
    }

    /// <summary>
    /// Channel Host 声明负责的 channelId。同 channel 后注册者生效，出站只发给当前连接。
    /// 能力随注册帧透传，出站代理按同一份能力回复。
    /// </summary>
    public void RegisterChannel(
        string connectionId,
        string channelId,
        ChannelCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(connectionId))
            return;

        var key = channelId.Trim();
        _channelOwners[key] = connectionId;
        var channels = _connectionChannels.GetOrAdd(
            connectionId,
            _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));
        channels[key] = 0;

        if (_adapterRegistry is null)
            return;

        _adapterRegistry.Register(new RemoteChannelAdapterProxy(key, capabilities, PushChannelOutbound));
        _logger.LogDebug("Channel adapter registered: {Channel} connection={ConnectionId}", key, connectionId);
    }

    private void ReleaseChannelOwnership(string connectionId)
    {
        if (!_connectionChannels.TryRemove(connectionId, out var channels))
            return;

        foreach (var channelId in channels.Keys)
        {
            if (!_channelOwners.TryGetValue(channelId, out var owner) ||
                !string.Equals(owner, connectionId, StringComparison.Ordinal))
                continue;

            _channelOwners.TryRemove(channelId, out _);
            _adapterRegistry?.Unregister(channelId);
        }
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

    /// <summary>
    /// 向该 channel 当前注册连接推送出站请求（不广播历史连接）。
    /// </summary>
    /// <returns>是否已把帧交给当前连接</returns>
    public bool PushChannelOutbound(GatewayChannelOutboundPayload payload)
    {
        if (string.IsNullOrWhiteSpace(payload.Channel))
            return false;

        if (!_channelOwners.TryGetValue(payload.Channel.Trim(), out var connectionId))
            return false;

        if (!_connections.TryGetValue(connectionId, out var connection))
            return false;

        var frame = GatewayWsFrameSerializer.Create(
            GatewayWsFrameType.ChannelOutbound,
            payload: payload);
        _ = connection.SendFrameAsync(frame);
        return true;
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
