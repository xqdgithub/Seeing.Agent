using Seeing.Agent.Abstractions.Interactions;
using Seeing.Agent.Hosting.Web.Circuits;

namespace Seeing.Agent.WebUI.Services;

/// <summary>宿主请求派发（Scoped，circuit 维度）：收件箱投影 → 活跃面板 → 同步编组执行 → 回传。</summary>
public sealed class HostRequestDispatcher : IDisposable
{
    private static readonly TimeSpan DefaultDispatchDelay = TimeSpan.FromMilliseconds(20);

    private readonly HostRequestInbox _inbox;
    private readonly HostRequestActivePanelRegistry _panels;
    private readonly IHostRequestManager _manager;
    private readonly TimeSpan _delay;
    private readonly ILogger<HostRequestDispatcher>? _logger;
    private readonly string _circuitId;
    private readonly bool _enabled;

    private int _dirty;
    private int _scheduled;
    private bool _disposed;

    public HostRequestDispatcher(
        HostRequestInbox inbox,
        HostRequestActivePanelRegistry panels,
        IHostRequestManager manager,
        CircuitContext circuitContext,
        ILogger<HostRequestDispatcher>? logger = null)
        : this(inbox, panels, manager, circuitContext.Id ?? string.Empty, DefaultDispatchDelay, logger) { }

    internal HostRequestDispatcher(
        HostRequestInbox inbox,
        HostRequestActivePanelRegistry panels,
        IHostRequestManager manager,
        string circuitId,
        TimeSpan delay,
        ILogger<HostRequestDispatcher>? logger = null)
    {
        _inbox = inbox ?? throw new ArgumentNullException(nameof(inbox));
        _panels = panels ?? throw new ArgumentNullException(nameof(panels));
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _circuitId = circuitId;
        _delay = delay;
        _logger = logger;

        if (string.IsNullOrEmpty(circuitId))
            return; // prerender / 无 circuit：不订阅、不派发

        _enabled = true;
        _inbox.Changed += OnChanged;
        Schedule(); // 构造时初始派发
    }

    private void OnChanged() => Schedule();

    private void Schedule()
    {
        if (_disposed || !_enabled) return;
        Volatile.Write(ref _dirty, 1);
        if (Interlocked.CompareExchange(ref _scheduled, 1, 0) == 1) return;
        _ = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        try
        {
            if (_delay > TimeSpan.Zero)
                await Task.Delay(_delay).ConfigureAwait(false);
            while (Interlocked.Exchange(ref _dirty, 0) == 1)
                await DispatchOnceAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "宿主请求派发失败");
        }
        finally
        {
            Interlocked.Exchange(ref _scheduled, 0);
            if (!_disposed && Volatile.Read(ref _dirty) == 1)
                Schedule();
        }
    }

    private async Task DispatchOnceAsync()
    {
        if (_disposed) return;
        foreach (var req in _inbox.GetAll())
        {
            if (_panels.TryGetLocal(req.SessionId, _circuitId, out var target) && target is not null)
            {
                HostResponse resp;
                try
                {
                    resp = await target(req, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    resp = new HostResponse
                    {
                        RequestId = req.RequestId,
                        SessionId = req.SessionId,
                        Status = HostResponseStatus.Failed,
                        Reason = ex.Message
                    };
                }
                _manager.TryResolve(req.RequestId, resp, req.SessionId);
            }
            else if (!_panels.HasAny(req.SessionId))
            {
                _manager.TryResolve(req.RequestId, new HostResponse
                {
                    RequestId = req.RequestId,
                    SessionId = req.SessionId,
                    Status = HostResponseStatus.Unavailable
                }, req.SessionId);
            }
            // else: 他 circuit 持有 → 跳过
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_enabled)
            _inbox.Changed -= OnChanged;
    }
}
