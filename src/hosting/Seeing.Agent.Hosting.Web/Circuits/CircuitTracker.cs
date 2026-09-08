using System.Collections.Concurrent;

namespace Seeing.Agent.Hosting.Web.Circuits;

/// <summary>
/// 追踪存活 Circuit 集合，组件可查询是否已断连。
/// </summary>
public sealed class CircuitTracker
{
    private readonly ConcurrentDictionary<string, byte> _liveCircuits = new();

    public void Register(string circuitId) => _liveCircuits.TryAdd(circuitId, 0);
    public void Remove(string circuitId) => _liveCircuits.TryRemove(circuitId, out _);
    public bool IsAlive(string circuitId) => _liveCircuits.ContainsKey(circuitId);
}
