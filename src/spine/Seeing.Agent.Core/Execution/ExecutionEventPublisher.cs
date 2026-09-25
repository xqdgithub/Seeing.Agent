using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using System.Threading.Channels;
using Seeing.Agent.Abstractions.Events;
using Microsoft.Extensions.Logging;

namespace Seeing.Agent.Core.Execution;

/// <summary>
/// Implementation of execution event publisher using per-subscriber channel.
/// Supports multiple subscribers and event buffering for reconnection.
/// <para>
/// 一致性：<see cref="Publish"/> 的缓冲区写入与订阅者扇出、<see cref="SubscribeAsync"/> 的
/// 订阅注册与缓冲区回放均在同一 <c>_lock</c> 临界区内完成，保证订阅者在建立瞬间对每个事件
/// 「恰好一次」——订阅前发布的事件只经回放送达，订阅后发布的事件只经实时扇出送达，不丢不重。
/// </para>
/// </summary>
public class ExecutionEventPublisher : IExecutionEventPublisher, IDisposable
{
    private readonly ConcurrentDictionary<string, CircularBuffer<IMessageEvent>> _buffers = new();
    private readonly ConcurrentDictionary<string, List<ChannelWriter<IMessageEvent>>> _subscribers = new();
    private readonly ExecutionOptions _options;
    private readonly ILogger<ExecutionEventPublisher> _logger;
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>
    /// Creates a new ExecutionEventPublisher with the specified options.
    /// </summary>
    public ExecutionEventPublisher(ExecutionOptions options, ILogger<ExecutionEventPublisher> logger)
    {
        _options = options ?? new ExecutionOptions();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public void Publish(string sessionId, IMessageEvent evt)
    {
        if (string.IsNullOrEmpty(sessionId) || evt == null)
            return;

        lock (_lock)
        {
            // 缓冲区写入与订阅者扇出同锁：订阅注册（含回放）不可能落在两者之间，
            // 否则事件会同时经回放与扇出送达（恰好一次语义被破坏）。
            var buffer = _buffers.GetOrAdd(sessionId, _ => new CircularBuffer<IMessageEvent>(_options.EventBufferSize));
            buffer.Add(evt);

            if (_subscribers.TryGetValue(sessionId, out var subscribers))
            {
                foreach (var writer in subscribers.ToList())
                {
                    writer.TryWrite(evt);
                }
            }
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<IMessageEvent> SubscribeAsync(
        string sessionId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(sessionId))
            yield break;

        // Create a dedicated channel for this subscriber
        var subscriberChannel = Channel.CreateUnbounded<IMessageEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        // 注册与回放同锁：两者与 Publish 的（缓冲区写入 + 扇出）互斥，
        // 从而订阅前事件仅回放一次、订阅后事件仅扇出一次，消除双份推送窗口。
        lock (_lock)
        {
            var subscribers = _subscribers.GetOrAdd(sessionId, _ => new List<ChannelWriter<IMessageEvent>>());
            subscribers.Add(subscriberChannel.Writer);

            var buffer = _buffers.GetOrAdd(sessionId, _ => new CircularBuffer<IMessageEvent>(_options.EventBufferSize));
            foreach (var evt in buffer.GetAll())
            {
                subscriberChannel.Writer.TryWrite(evt);
            }
        }

        try
        {
            await foreach (var evt in subscriberChannel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return evt;
            }
        }
        finally
        {
            // Unregister subscriber
            lock (_lock)
            {
                if (_subscribers.TryGetValue(sessionId, out var subscribers))
                {
                    subscribers.Remove(subscriberChannel.Writer);
                    // 最后一个订阅者退出时移除空列表条目，避免会话级 _subscribers 无界增长
                    if (subscribers.Count == 0)
                        _subscribers.TryRemove(sessionId, out _);
                }
            }
            subscriberChannel.Writer.TryComplete();
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<IMessageEvent> GetBufferedEvents(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return new List<IMessageEvent>();

        lock (_lock)
        {
            var buffer = _buffers.GetOrAdd(sessionId, _ => new CircularBuffer<IMessageEvent>(_options.EventBufferSize));
            return buffer.GetAll();
        }
    }

    /// <inheritdoc/>
    public void ClearBuffer(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return;

        lock (_lock)
        {
            if (_buffers.TryGetValue(sessionId, out var buffer))
            {
                buffer.Clear();
            }
        }
    }

    /// <inheritdoc/>
    public void CompleteSession(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return;

        lock (_lock)
        {
            // Complete all subscriber channels
            if (_subscribers.TryRemove(sessionId, out var subscribers))
            {
                foreach (var writer in subscribers)
                {
                    writer.TryComplete();
                }
            }

            // 移除并清空缓冲：会话终结后不得残留 CircularBuffer 条目，
            // 否则长驻宿主按会话无界增长（每会话 100 引用）。
            if (_buffers.TryRemove(sessionId, out var buffer))
            {
                buffer.Clear();
            }
        }
    }

    /// <summary>
    /// 当前缓冲条目数（诊断用，供测试断言会话终结后不残留）。
    /// </summary>
    internal int BufferEntryCount => _buffers.Count;

    /// <summary>
    /// 当前订阅者会话条目数（诊断用，供测试断言最后一个订阅者退出后不残留）。
    /// </summary>
    internal int SubscriberEntryCount => _subscribers.Count;

    /// <summary>
    /// Disposes all resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        lock (_lock)
        {
            // Complete all subscribers
            foreach (var (_, subscribers) in _subscribers)
            {
                foreach (var writer in subscribers)
                {
                    writer.TryComplete();
                }
            }
            _subscribers.Clear();

            _buffers.Clear();
        }
    }
}
