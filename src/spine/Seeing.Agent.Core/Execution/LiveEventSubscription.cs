using Seeing.Agent.Abstractions.Events;
using System.Threading.Channels;

namespace Seeing.Agent.Core.Execution;

/// <summary>
/// 实时订阅句柄实现：由 <see cref="ExecutionEventPublisher.SubscribeLive"/> 创建。
/// 不回放缓冲历史；<see cref="Dispose"/> 从发布器注销并收口，可安全多次调用。
/// </summary>
public sealed class LiveEventSubscription : ILiveEventSubscription
{
    private readonly ChannelReader<IMessageEvent> _reader;
    private readonly Action _onDispose;
    private int _disposed;

    /// <summary>空订阅：已收口、无事件（Null 发布器及非法 sessionId 兜底）。</summary>
    public static ILiveEventSubscription Completed { get; } =
        CreateCompleted();

    internal LiveEventSubscription(ChannelReader<IMessageEvent> reader, Action onDispose)
    {
        _reader = reader;
        _onDispose = onDispose;
    }

    public IAsyncEnumerator<IMessageEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
        _reader.ReadAllAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        _onDispose();
    }

    private static ILiveEventSubscription CreateCompleted()
    {
        var channel = Channel.CreateUnbounded<IMessageEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        channel.Writer.TryComplete();
        return new LiveEventSubscription(channel.Reader, static () => { });
    }
}
