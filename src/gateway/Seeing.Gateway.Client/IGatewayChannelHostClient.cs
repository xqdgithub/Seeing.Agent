using Seeing.Agent.Abstractions.Channels;
using Seeing.Gateway.Models;
using Seeing.Gateway.Protocol;

namespace Seeing.Gateway.Client;

/// <summary>
/// 通道宿主面：与 <see cref="ISessionClient"/> 共用同一条 WebSocket（单连接双面）。
/// </summary>
public interface IGatewayChannelHostClient
{
    string ChannelId { get; }

    bool IsConnected { get; }

    event Func<GatewayChannelOutboundPayload, CancellationToken, Task>? OnChannelOutbound;

    /// <summary>连接即 channel.register v2（注册帧携带调用方提供的能力）。</summary>
    Task ConnectAsync(string channelId, ChannelCapabilities capabilities, CancellationToken ct = default);

    Task<GatewaySessionResetResult> ResetSessionAsync(string sessionId, CancellationToken ct = default);

    Task<IReadOnlyList<GatewayPendingPermission>> GetPendingPermissionsAsync(
        string sessionId, CancellationToken ct = default);
}
