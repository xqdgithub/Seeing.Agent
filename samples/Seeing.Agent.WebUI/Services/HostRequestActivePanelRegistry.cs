using System.Collections.Concurrent;
using Seeing.Agent.Abstractions.Interactions;

namespace Seeing.Agent.WebUI.Services;

/// <summary>活跃文件面板登记（Singleton）：按 (sessionId, circuitId) 存可编组委托。</summary>
public sealed class HostRequestActivePanelRegistry
{
    private readonly ConcurrentDictionary<(string SessionId, string CircuitId),
        Func<HostRequest, CancellationToken, Task<HostResponse>>> _panels = new();

    /// <summary>登记/覆盖某 circuit 的会话活跃面板委托。</summary>
    public void Register(string sessionId, string circuitId,
        Func<HostRequest, CancellationToken, Task<HostResponse>> handler)
        => _panels[(sessionId, circuitId)] = handler;

    /// <summary>注销某 circuit 的会话活跃面板。</summary>
    public void Unregister(string sessionId, string circuitId)
        => _panels.TryRemove((sessionId, circuitId), out _);

    /// <summary>取本 circuit 的会话活跃面板委托（原子，无 HasLocal/GetLocal 二步窗口）。</summary>
    public bool TryGetLocal(string sessionId, string circuitId,
        out Func<HostRequest, CancellationToken, Task<HostResponse>>? handler)
        => _panels.TryGetValue((sessionId, circuitId), out handler);

    /// <summary>是否存在任一 circuit 持有该会话的活跃面板。</summary>
    public bool HasAny(string sessionId)
    {
        foreach (var key in _panels.Keys)
            if (string.Equals(key.SessionId, sessionId, StringComparison.Ordinal))
                return true;
        return false;
    }
}
