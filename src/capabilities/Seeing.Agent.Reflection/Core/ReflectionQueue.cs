using Microsoft.Extensions.Options;
using Seeing.Agent.Reflection.Abstractions;
using Seeing.Agent.Reflection.Configuration;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Seeing.Agent.Reflection.Core;

/// <summary>
/// 基于 <see cref="Channel{T}"/> 的复盘队列（进程内、有界、按会话去重）。
/// <para>
/// 实现要点：通道里只存 <c>SessionId</c>，真正的最新请求存在
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> 里。这样同一会话重复入队时
/// 只需覆盖字典值——通道中的排队位置保持不变，不会把该会话挪到队尾。
/// 取出时以字典中的最新请求为准，取到后立即移除，因此同一会话可在下次重新入队。
/// </para>
/// </summary>
public sealed class ReflectionQueue : IReflectionQueue
{
    private readonly ConcurrentDictionary<string, ReflectionRequest> _pending = new(StringComparer.Ordinal);
    private readonly Channel<string> _channel;

    /// <summary>DI 构造：容量取自 <see cref="ReflectionOptions.QueueCapacity"/>。</summary>
    public ReflectionQueue(IOptionsMonitor<ReflectionOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var capacity = Math.Max(1, options.CurrentValue.QueueCapacity);
        _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            // 满时由 TryWrite 返回 false，不阻塞入队方（终态观察者必须毫秒级返回）。
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    /// <summary>直接指定容量的构造（测试便捷入口）。</summary>
    public ReflectionQueue(int capacity)
    {
        _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(Math.Max(1, capacity))
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    /// <inheritdoc />
    public int PendingCount => _pending.Count;

    /// <inheritdoc />
    /// <remarks>
    /// 以「队列里那批 SessionId」为序取字典值：字典里可能有已出队但尚未被 TryDequeue 摘除的条目
    /// （极窄窗口），按通道快照能保证展示的正是「还没开始跑的那些」。
    /// </remarks>
    public IReadOnlyList<ReflectionRequest> Snapshot()
    {
        // Channel 不支持无消费窥视，因此以字典为准；字典与通道的一致性由 TryEnqueue/TryDequeue 维护：
        // 「字典有值 ⇔ 通道有槽」，所以字典的值就是排队集合的超集（仅在出队窗口内可能短暂多一项）。
        return _pending.Values
            .OrderBy(r => r.At)
            .ToList();
    }

    /// <inheritdoc />
    public ReflectionEnqueueResult TryEnqueue(ReflectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.SessionId))
            return ReflectionEnqueueResult.Invalid;

        // 已存在：覆盖内容、保留原位置。这是正常去重，不是失败。
        if (!_pending.TryAdd(request.SessionId, request))
        {
            _pending[request.SessionId] = request;
            return ReflectionEnqueueResult.Updated;
        }

        if (_channel.Writer.TryWrite(request.SessionId))
            return ReflectionEnqueueResult.Enqueued;

        // 队列满：回滚字典，保证「字典有值」与「通道有槽」严格对应。
        _pending.TryRemove(request.SessionId, out _);
        return ReflectionEnqueueResult.Dropped;
    }

    /// <inheritdoc />
    public bool TryDequeue(out ReflectionRequest request)
    {
        while (_channel.Reader.TryRead(out var sessionId))
        {
            if (_pending.TryRemove(sessionId, out var latest))
            {
                request = latest;
                return true;
            }
        }

        request = null!;
        return false;
    }

    /// <inheritdoc />
    public async ValueTask<ReflectionRequest> DequeueAsync(CancellationToken ct)
    {
        while (true)
        {
            var sessionId = await _channel.Reader.ReadAsync(ct).ConfigureAwait(false);
            if (_pending.TryRemove(sessionId, out var latest))
                return latest;
        }
    }
}
