using System.Collections.Concurrent;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 监听 Circuit 生命周期，断连时取消关联操作。
/// 配合 JSInterop catch 块减少 JSDisconnectedException 日志刷屏。
/// 在 OnCircuitOpenedAsync 写入 CircuitContext.Id，供页面经 Router 关联消费者；
/// 在 OnCircuitClosedAsync 调用 Router.DetachAllForCircuit 释放该 circuit 的 scope 与订阅。
/// </summary>
public sealed class SeeingCircuitHandler : CircuitHandler
{
    private readonly ILogger<SeeingCircuitHandler> _logger;
    private readonly CircuitTracker _tracker;
    private readonly CircuitContext _circuitContext;
    private readonly SessionEventStreamRouter _router;

    public SeeingCircuitHandler(
        ILogger<SeeingCircuitHandler> logger,
        CircuitTracker tracker,
        CircuitContext circuitContext,
        SessionEventStreamRouter router)
    {
        _logger = logger;
        _tracker = tracker;
        _circuitContext = circuitContext;
        _router = router;
    }

    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _tracker.Register(circuit.Id);
        _circuitContext.Id = circuit.Id;
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _tracker.Remove(circuit.Id);
        try
        {
            _router.DetachAllForCircuit(circuit.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "关闭 circuit 时释放事件流资源失败: {CircuitId}", circuit.Id);
        }
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
