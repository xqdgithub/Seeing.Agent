using Seeing.Agent.Abstractions.Interactions;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 全局宿主请求收件箱（Singleton，只读投影，非权威）。
/// <para>
/// 输入：<see cref="IHostRequestManager.GetAllPending"/> + <see cref="IHostRequestManager.PendingChanged"/>；
/// 输出：<see cref="HostRequest"/> 全量快照。每次通知全量重建，天然对账。
/// </para>
/// <para>并发契约：内部 <c>_gate</c> 串行写；<see cref="Changed"/> 锁外触发；不持有任何 Scoped 引用。</para>
/// </summary>
public sealed class HostRequestInbox : IDisposable
{
    private readonly IHostRequestManager _manager;
    private readonly ILogger<HostRequestInbox>? _logger;
    private readonly object _gate = new();

    private IReadOnlyList<HostRequest> _requests = Array.Empty<HostRequest>();
    private bool _disposed;

    public HostRequestInbox(IHostRequestManager manager, ILogger<HostRequestInbox>? logger = null)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _logger = logger;
        _manager.PendingChanged += OnPendingChanged;
        Rebuild();
    }

    /// <summary>投影变更通知（锁外触发）。</summary>
    public event Action? Changed;

    /// <summary>全部在途宿主请求快照。</summary>
    public IReadOnlyList<HostRequest> GetAll()
    {
        lock (_gate)
            return _requests;
    }

    private void OnPendingChanged() => Rebuild();

    private void Rebuild()
    {
        if (_disposed)
            return;

        IReadOnlyList<HostRequest> pending;
        try
        {
            pending = _manager.GetAllPending();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "读取全局在途宿主请求失败");
            return;
        }

        var requests = pending
            .Where(r => !string.IsNullOrEmpty(r.RequestId))
            .ToList();

        var changed = false;
        lock (_gate)
        {
            if (!SameIds(_requests, requests))
            {
                _requests = requests;
                changed = true;
            }
        }

        if (changed)
            NotifyChanged();
    }

    private static bool SameIds(IReadOnlyList<HostRequest> current, IReadOnlyList<HostRequest> next)
    {
        if (current.Count != next.Count)
            return false;

        for (var i = 0; i < current.Count; i++)
        {
            if (!string.Equals(current[i].RequestId, next[i].RequestId, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private void NotifyChanged()
    {
        if (_disposed)
            return;

        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "宿主请求收件箱变更通知失败");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _manager.PendingChanged -= OnPendingChanged;
        Changed = null;
        lock (_gate)
            _requests = Array.Empty<HostRequest>();
    }
}
