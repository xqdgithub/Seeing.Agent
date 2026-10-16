using System.Collections.Concurrent;

namespace Seeing.Gateway.Client;

/// <summary>
/// 按会话跟踪在途订阅。同一 sessionId 可并存多条订阅，互不覆盖。
/// <para><see cref="UnregisterRun"/> 只移除并释放自己的 CTS；<see cref="CancelRun"/> 取消该会话全部在途。</para>
/// </summary>
public sealed class GatewayRunTracker
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, CancellationTokenSource>> _runs = new();

    /// <summary>登记一条订阅。CTS 由订阅方在 try/finally 里经 <see cref="UnregisterRun"/> 释放。</summary>
    public (string RunId, CancellationToken Token) RegisterRun(string sessionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        var runId = Guid.NewGuid().ToString("N");
        var cts = new CancellationTokenSource();
        var runs = _runs.GetOrAdd(sessionId, _ => new ConcurrentDictionary<string, CancellationTokenSource>());
        runs[runId] = cts;
        return (runId, cts.Token);
    }

    /// <summary>取消该会话全部在途订阅。无在途时返回 false。</summary>
    public bool CancelRun(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId) || !_runs.TryGetValue(sessionId, out var runs))
            return false;

        var any = false;
        foreach (var cts in runs.Values.ToArray())
        {
            try
            {
                cts.Cancel();
                any = true;
            }
            catch (ObjectDisposedException)
            {
                // 订阅已释放自己的 CTS
            }
        }

        return any;
    }

    /// <summary>是否仍有该会话的在途订阅。</summary>
    public bool IsRunning(string sessionId) =>
        !string.IsNullOrEmpty(sessionId)
        && _runs.TryGetValue(sessionId, out var runs)
        && !runs.IsEmpty;

    /// <summary>只移除并释放指定订阅的 CTS，不影响同会话其它订阅。</summary>
    public void UnregisterRun(string sessionId, string runId)
    {
        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(runId))
            return;

        if (!_runs.TryGetValue(sessionId, out var runs))
            return;

        if (runs.TryRemove(runId, out var cts))
            cts.Dispose();

        if (runs.IsEmpty)
        {
            _runs.TryRemove(
                new KeyValuePair<string, ConcurrentDictionary<string, CancellationTokenSource>>(sessionId, runs));
        }
    }
}
