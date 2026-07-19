using System.Collections.Concurrent;

namespace Seeing.Agent.Gateway.Core;

/// <summary>
/// 跟踪活跃 Gateway 执行，支持按 executionId 取消。
/// </summary>
public sealed class GatewayRunTracker
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeRuns = new();
    private readonly ConcurrentDictionary<string, string> _executionToSession = new();

    /// <summary>注册新的执行并返回其取消令牌源</summary>
    public CancellationTokenSource RegisterRun(string executionId, string sessionId)
    {
        var cts = new CancellationTokenSource();
        _activeRuns[executionId] = cts;
        _executionToSession[executionId] = sessionId;
        return cts;
    }

    /// <summary>取消指定执行</summary>
    public bool CancelRun(string executionId)
    {
        if (!_activeRuns.TryGetValue(executionId, out var cts))
            return false;

        cts.Cancel();
        return true;
    }

    /// <summary>是否有活跃执行</summary>
    public bool IsRunning(string executionId) => _activeRuns.ContainsKey(executionId);

    /// <summary>查询 execution 所属 session</summary>
    public bool TryGetSessionId(string executionId, out string? sessionId) =>
        _executionToSession.TryGetValue(executionId, out sessionId);

    /// <summary>注销执行并释放资源</summary>
    public void UnregisterRun(string executionId)
    {
        _executionToSession.TryRemove(executionId, out _);
        if (_activeRuns.TryRemove(executionId, out var cts))
            cts.Dispose();
    }
}
