using System.Collections.Concurrent;

namespace Seeing.Gateway.WeCom;

/// <summary>
/// 跟踪进行中的企微流式回复，用于处理 <c>msgtype=stream</c> 刷新回调（Webhook 模式）。
/// 长连接模式依赖主动推送，见 <see cref="WeComStreamState"/> 的 ProcessingKeepalive。
/// </summary>
public sealed class WeComActiveStreamRegistry
{
    private readonly ConcurrentDictionary<string, IWeComActiveStreamHandle> _streams = new();

    public void Register(string streamId, IWeComActiveStreamHandle handle) =>
        _streams[streamId] = handle;

    public void Unregister(string streamId, IWeComActiveStreamHandle handle)
    {
        if (_streams.TryGetValue(streamId, out var existing) && ReferenceEquals(existing, handle))
            _streams.TryRemove(streamId, out _);
    }

    public bool TryHandleRefresh(WeComWsFrame refreshFrame, string streamId, CancellationToken cancellationToken)
    {
        if (!_streams.TryGetValue(streamId, out var handle))
            return false;

        _ = handle.HandleRefreshAsync(refreshFrame, cancellationToken);
        return true;
    }

    public async Task PauseAllAsync(CancellationToken cancellationToken)
    {
        foreach (var handle in _streams.Values)
        {
            try
            {
                await handle.NotifyConnectionDegradedAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // best effort
            }
        }
    }

    public async Task FlushAllAsync(CancellationToken cancellationToken)
    {
        foreach (var handle in _streams.Values)
        {
            try
            {
                await handle.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // best effort
            }
        }
    }

    public async Task AbortAllAsync(string reason, CancellationToken cancellationToken)
    {
        foreach (var handle in _streams.Values)
        {
            try
            {
                await handle.AbortAsync(reason, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // best effort
            }
        }

        _streams.Clear();
    }
}

public interface IWeComActiveStreamHandle
{
    Task HandleRefreshAsync(WeComWsFrame refreshFrame, CancellationToken cancellationToken);

    Task NotifyConnectionDegradedAsync(CancellationToken cancellationToken);

    Task FlushAsync(CancellationToken cancellationToken);

    Task AbortAsync(string reason, CancellationToken cancellationToken);
}
