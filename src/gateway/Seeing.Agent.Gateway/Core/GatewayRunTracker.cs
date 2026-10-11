using System.Collections.Concurrent;

namespace Seeing.Agent.Gateway.Core;

/// <summary>
/// 跟踪活跃 Gateway 会话订阅，支持按 sessionId 取消。
/// </summary>
public sealed class GatewayRunTracker
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeRuns = new();

    /// <summary>注册新的会话订阅并返回其取消令牌源</summary>
    public CancellationTokenSource RegisterRun(string sessionId)
    {
        var cts = new CancellationTokenSource();
        _activeRuns[sessionId] = cts;
        return cts;
    }

    /// <summary>取消指定会话订阅</summary>
    public bool CancelRun(string sessionId)
    {
        if (!_activeRuns.TryGetValue(sessionId, out var cts))
            return false;

        cts.Cancel();
        return true;
    }

    /// <summary>是否有活跃会话订阅</summary>
    public bool IsRunning(string sessionId) => _activeRuns.ContainsKey(sessionId);

    /// <summary>注销会话订阅并释放资源</summary>
    public void UnregisterRun(string sessionId)
    {
        if (_activeRuns.TryRemove(sessionId, out var cts))
            cts.Dispose();
    }
}
