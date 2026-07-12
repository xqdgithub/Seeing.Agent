using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using System.Threading.Channels;
using Seeing.Agent.Core.Events;

namespace Seeing.Agent.App.Execution;

/// <summary>
/// Implementation of execution event publisher using Channel per session.
/// Supports multiple subscribers and event buffering for reconnection.
/// </summary>
public class ExecutionEventPublisher : IExecutionEventPublisher, IDisposable
{
    private readonly ConcurrentDictionary<string, Channel<IMessageEvent>> _channels = new();
    private readonly ConcurrentDictionary<string, CircularBuffer<IMessageEvent>> _buffers = new();
    private readonly ConcurrentDictionary<string, List<ChannelWriter<IMessageEvent>>> _subscribers = new();
    private readonly ExecutionOptions _options;
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>
    /// Creates a new ExecutionEventPublisher with the specified options.
    /// </summary>
    public ExecutionEventPublisher(ExecutionOptions options)
    {
        _options = options ?? new ExecutionOptions();
    }

    /// <inheritdoc/>
    public void Publish(string sessionId, IMessageEvent evt)
    {
        if (string.IsNullOrEmpty(sessionId) || evt == null)
            return;

        // Add to buffer for reconnection support
        var buffer = _buffers.GetOrAdd(sessionId, _ => new CircularBuffer<IMessageEvent>(_options.EventBufferSize));
        buffer.Add(evt);

        // Get or create channel for the session
        var channel = _channels.GetOrAdd(sessionId, _ => Channel.CreateUnbounded<IMessageEvent>(
            new UnboundedChannelOptions { SingleReader = false, SingleWriter = false }));

        // Write to channel (non-blocking)
        channel.Writer.TryWrite(evt);

        // Also write to all direct subscribers
        lock (_lock)
        {
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

        // Register subscriber
        lock (_lock)
        {
            var subscribers = _subscribers.GetOrAdd(sessionId, _ => new List<ChannelWriter<IMessageEvent>>());
            subscribers.Add(subscriberChannel.Writer);
        }

        // Send buffered events first (for reconnection)
        var buffer = _buffers.GetOrAdd(sessionId, _ => new CircularBuffer<IMessageEvent>(_options.EventBufferSize));
        foreach (var evt in buffer.GetAll())
        {
            subscriberChannel.Writer.TryWrite(evt);
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

        var buffer = _buffers.GetOrAdd(sessionId, _ => new CircularBuffer<IMessageEvent>(_options.EventBufferSize));
        return buffer.GetAll();
    }

    /// <inheritdoc/>
    public void ClearBuffer(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return;

        if (_buffers.TryGetValue(sessionId, out var buffer))
        {
            buffer.Clear();
        }
    }

    /// <inheritdoc/>
    public void CompleteSession(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return;

        // Complete the main channel
        if (_channels.TryRemove(sessionId, out var channel))
        {
            channel.Writer.TryComplete();
        }

        // Complete all subscriber channels
        lock (_lock)
        {
            if (_subscribers.TryRemove(sessionId, out var subscribers))
            {
                foreach (var writer in subscribers)
                {
                    writer.TryComplete();
                }
            }
        }

        // Clear the buffer
        ClearBuffer(sessionId);
    }

    /// <summary>
    /// Disposes all resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        // Complete all channels (take snapshot to avoid collection modified exception)
        var channelSnapshot = _channels.ToArray();
        foreach (var (sessionId, channel) in channelSnapshot)
        {
            channel.Writer.TryComplete();
        }
        _channels.Clear();

        // Complete all subscribers
        lock (_lock)
        {
            var subscriberSnapshot = _subscribers.ToArray();
            foreach (var (_, subscribers) in subscriberSnapshot)
            {
                foreach (var writer in subscribers)
                {
                    writer.TryComplete();
                }
            }
            _subscribers.Clear();
        }

        _buffers.Clear();
    }
}