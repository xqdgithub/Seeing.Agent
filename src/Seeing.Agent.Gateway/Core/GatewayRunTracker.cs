using System.Collections.Concurrent;

namespace Seeing.Agent.Gateway.Core;

/// <summary>
/// 跟踪活跃 Gateway 聊天运行，支持按 sessionId 取消。
/// </summary>
public sealed class GatewayRunTracker
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeRuns = new();

    /// <summary>注册新的运行并返回其取消令牌源</summary>
    public CancellationTokenSource RegisterRun(string sessionId)
    {
        var cts = new CancellationTokenSource();
        _activeRuns[sessionId] = cts;
        return cts;
    }

    /// <summary>取消指定会话的活跃运行</summary>
    public bool StopRun(string sessionId)
    {
        if (!_activeRuns.TryGetValue(sessionId, out var cts))
            return false;

        cts.Cancel();
        return true;
    }

    /// <summary>是否有活跃运行</summary>
    public bool IsRunning(string sessionId) => _activeRuns.ContainsKey(sessionId);

    /// <summary>注销运行并释放资源</summary>
    public void UnregisterRun(string sessionId)
    {
        if (_activeRuns.TryRemove(sessionId, out var cts))
        {
            cts.Dispose();
        }
    }
}
