namespace Seeing.Gateway;

/// <summary>
/// 通道桥接契约（将外部通道接入 Gateway）
/// </summary>
public interface IChannelBridge
{
    string ChannelId { get; }

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}
