using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.Logging;

namespace Seeing.Agent.Hosting.Web.Circuits;

/// <summary>
/// 监听 Circuit 生命周期：打开时写入 <see cref="CircuitContext"/>，关闭时清理宿主资源。
/// </summary>
public sealed class SeeingCircuitHandler : CircuitHandler
{
    private readonly ILogger<SeeingCircuitHandler> _logger;
    private readonly CircuitTracker _tracker;
    private readonly CircuitContext _circuitContext;
    private readonly ICircuitResourceCleanup? _cleanup;

    public SeeingCircuitHandler(
        ILogger<SeeingCircuitHandler> logger,
        CircuitTracker tracker,
        CircuitContext circuitContext,
        ICircuitResourceCleanup? cleanup = null)
    {
        _logger = logger;
        _tracker = tracker;
        _circuitContext = circuitContext;
        _cleanup = cleanup;
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
        if (_cleanup is null)
            return Task.CompletedTask;

        try
        {
            _cleanup.DetachAllForCircuit(circuit.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "关闭 circuit 时释放事件流资源失败: {CircuitId}", circuit.Id);
        }

        return Task.CompletedTask;
    }
}
