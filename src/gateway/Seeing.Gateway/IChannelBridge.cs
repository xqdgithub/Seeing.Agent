using Seeing.Agent.Abstractions.Channels;

namespace Seeing.Gateway;

/// <summary>
/// 通道桥接契约（将外部通道接入 Gateway）
/// </summary>
public interface IChannelBridge
{
    string ChannelId { get; }

    /// <summary>通道能力声明；注册帧与出站裁决共用同一份能力。</summary>
    ChannelCapabilities Capabilities { get; }

    /// <summary>
    /// Gateway 连接对象（可选）。Host 会统一管理连接生命周期：Connect → RegisterChannel → Dispose。
    /// </summary>
    IGatewayConnection? GatewayConnection { get; }

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}
