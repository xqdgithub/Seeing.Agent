namespace Seeing.Gateway.WeCom.Connection;

/// <summary>
/// 出站消息优先级（数值越大越优先）。
/// </summary>
public enum WeComOutboundPriority
{
    Ping = 0,
    Keepalive = 1,
    ContentDelta = 2,
    Finish = 3
}

/// <summary>
/// 连接级出站频率治理，防止触发企微约 30 条/分钟限流。
/// Keepalive 与 Finish 不占正文配额。
/// </summary>
public sealed class WeComOutboundGovernor
{
    internal const int MaxMessagesPerMinute = 25;

    private readonly object _lock = new();
    private readonly Queue<DateTime> _contentTimestamps = new();

    public async Task WaitForSlotAsync(WeComOutboundPriority priority, CancellationToken cancellationToken)
    {
        if (priority is WeComOutboundPriority.Finish or WeComOutboundPriority.Keepalive)
            return;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            TimeSpan? wait = null;
            lock (_lock)
            {
                Prune(DateTime.Now);
                if (_contentTimestamps.Count < MaxMessagesPerMinute)
                    return;

                wait = _contentTimestamps.Peek().AddMinutes(1) - DateTime.Now;
            }

            if (wait is { } delay && delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            else
                return;
        }
    }

    public void RecordSend(WeComOutboundPriority priority)
    {
        if (priority is WeComOutboundPriority.Finish or WeComOutboundPriority.Keepalive)
            return;

        lock (_lock)
        {
            _contentTimestamps.Enqueue(DateTime.Now);
            Prune(DateTime.Now);
        }
    }

    private void Prune(DateTime now)
    {
        while (_contentTimestamps.Count > 0 && _contentTimestamps.Peek() < now.AddMinutes(-1))
            _contentTimestamps.Dequeue();
    }
}
