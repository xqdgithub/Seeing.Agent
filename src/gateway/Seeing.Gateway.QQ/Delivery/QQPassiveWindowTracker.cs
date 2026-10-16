using Microsoft.Extensions.Options;

namespace Seeing.Gateway.QQ.Delivery;

/// <summary>被动窗口判定结果。</summary>
public sealed record PassiveWindowResult(bool CanPassive, bool MustDropMsgId);

/// <summary>
/// QQ 被动回复窗口：按 msg_id 登记接收时间与已被动回复次数。
/// 超窗或超条数则降级为主动模式（出站 body 不带 msg_id）。
/// </summary>
public sealed class QQPassiveWindowTracker
{
    private readonly TimeSpan _window;
    private readonly int _replyLimit;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    private readonly record struct Entry(DateTimeOffset ReceivedAt, int ReplyCount);

    public QQPassiveWindowTracker(IOptions<QQOptions> options)
        : this(
            TimeSpan.FromSeconds(Math.Max(0, options.Value.PassiveWindowSeconds)),
            Math.Max(0, options.Value.PassiveReplyLimitPerMsg))
    {
    }

    public QQPassiveWindowTracker(TimeSpan window, int replyLimit, Func<DateTimeOffset>? clock = null)
    {
        _window = window < TimeSpan.Zero ? TimeSpan.Zero : window;
        _replyLimit = replyLimit < 0 ? 0 : replyLimit;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public void RegisterInbound(string msgId, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(msgId))
            return;

        lock (_gate)
        {
            PurgeExpired(at);
            _entries[msgId] = new Entry(at, 0);
        }
    }

    public PassiveWindowResult TryAcquirePassive(string msgId) =>
        TryAcquirePassive(msgId, _clock());

    public PassiveWindowResult TryAcquirePassive(string msgId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(msgId))
            return new PassiveWindowResult(CanPassive: false, MustDropMsgId: true);

        lock (_gate)
        {
            PurgeExpired(now);
            if (!_entries.TryGetValue(msgId, out var entry))
                return new PassiveWindowResult(CanPassive: false, MustDropMsgId: true);

            if (now - entry.ReceivedAt > _window)
            {
                _entries.Remove(msgId);
                return new PassiveWindowResult(CanPassive: false, MustDropMsgId: true);
            }

            if (entry.ReplyCount >= _replyLimit)
                return new PassiveWindowResult(CanPassive: false, MustDropMsgId: true);

            _entries[msgId] = entry with { ReplyCount = entry.ReplyCount + 1 };
            return new PassiveWindowResult(CanPassive: true, MustDropMsgId: false);
        }
    }

    private void PurgeExpired(DateTimeOffset now)
    {
        if (_entries.Count == 0)
            return;

        List<string>? stale = null;
        foreach (var pair in _entries)
        {
            if (now - pair.Value.ReceivedAt > _window)
                (stale ??= []).Add(pair.Key);
        }

        if (stale == null)
            return;

        foreach (var key in stale)
            _entries.Remove(key);
    }
}
