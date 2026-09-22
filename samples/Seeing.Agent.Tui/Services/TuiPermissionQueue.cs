using Seeing.Agent.Abstractions.Permissions;

namespace Seeing.Agent.Tui.Services;

/// <summary>
/// 权限在途请求投影：订阅 <see cref="IPermissionRequestManager.PendingChanged"/> 维护快照，并回传裁决。
/// </summary>
public sealed class TuiPermissionQueue : IDisposable
{
    private readonly IPermissionRequestManager _manager;
    private IReadOnlyList<PermissionRequest> _pending = [];

    public TuiPermissionQueue(IPermissionRequestManager manager)
    {
        _manager = manager;
        _manager.PendingChanged += OnPendingChanged;
        Refresh();
    }

    public IReadOnlyList<PermissionRequest> Pending => _pending;

    public bool TryResolve(PermissionRequest request, PermissionEffect effect, PermissionGrantScope scope)
        => _manager.TryResolve(
            request.RequestId!,
            effect,
            scope,
            PermissionResolvedBy.User,
            expectedSessionId: request.SessionId);

    public void Dispose() => _manager.PendingChanged -= OnPendingChanged;

    private void OnPendingChanged() => Refresh();

    private void Refresh() => _pending = _manager.GetAllPending();
}
