using System.Collections.Concurrent;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 监听 Circuit 生命周期，断连时取消关联操作。
/// 配合 JSInterop catch 块减少 JSDisconnectedException 日志刷屏。
/// </summary>
public sealed class SeeingCircuitHandler : CircuitHandler
{
    private readonly ILogger<SeeingCircuitHandler> _logger;
    private readonly CircuitTracker _tracker;

    public SeeingCircuitHandler(ILogger<SeeingCircuitHandler> logger, CircuitTracker tracker)
    {
        _logger = logger;
        _tracker = tracker;
    }

    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _tracker.Register(circuit.Id);
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _tracker.Remove(circuit.Id);
        return Task.CompletedTask;
    }
}

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
