using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Seeing.Agent.Abstractions.Events;

namespace Seeing.Agent.Core.Execution;

/// <summary>
/// 进程内会话组事件总线：按组 ID 维护订阅通道，发布时向所有订阅者扇出。
/// <para>无订阅者时静默丢弃；订阅注册为同步完成（返回枚举器时即注册）。</para>
/// </summary>
public sealed class ChannelSessionGroupEventBus : ISessionGroupEventBus
{
    private readonly ConcurrentDictionary<string, List<Channel<SessionGroupChangedEvent>>> _subscribers =
        new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public void Publish(SessionGroupChangedEvent evt)
    {
        if (!_subscribers.TryGetValue(evt.GroupId, out var channels))
            return; // 无订阅者：静默丢弃

        Channel<SessionGroupChangedEvent>[] snapshot;
        lock (channels)
            snapshot = channels.ToArray();

        foreach (var channel in snapshot)
            channel.Writer.TryWrite(evt);
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<SessionGroupChangedEvent> SubscribeAsync(string groupId, CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<SessionGroupChangedEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        // 与注销清理竞争时重试：确保订阅注册到当前仍挂在该 group 下的列表中
        while (true)
        {
            var list = _subscribers.GetOrAdd(groupId, _ => new List<Channel<SessionGroupChangedEvent>>());
            lock (list)
            {
                if (!_subscribers.TryGetValue(groupId, out var current) || !ReferenceEquals(current, list))
                    continue;

                list.Add(channel);
            }
            break;
        }

        return ReadAsync(groupId, channel, ct);
    }

    private async IAsyncEnumerable<SessionGroupChangedEvent> ReadAsync(
        string groupId,
        Channel<SessionGroupChangedEvent> channel,
        [EnumeratorCancellation] CancellationToken ct)
    {
        try
        {
            await foreach (var evt in channel.Reader.ReadAllAsync(ct))
                yield return evt;
        }
        finally
        {
            if (_subscribers.TryGetValue(groupId, out var list))
            {
                lock (list)
                {
                    list.Remove(channel);
                    // 最后一个订阅者注销时移除空 key，避免无界增长
                    if (list.Count == 0)
                        _subscribers.TryRemove(
                            new KeyValuePair<string, List<Channel<SessionGroupChangedEvent>>>(groupId, list));
                }
            }
            channel.Writer.TryComplete();
        }
    }
}
