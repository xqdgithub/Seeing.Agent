using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Configuration;

namespace Seeing.Agent.Core.Permission;

/// <summary>
/// 工作区根变更时清空全部话白名单与权限记忆，防止权限漂移。
/// </summary>
public sealed class WorkspaceBoundaryLifecycle : IDisposable
{
    private readonly IWorkspaceProvider _workspace;
    private readonly IPermissionGrantStore _grantStore;
    private bool _attached;

    public WorkspaceBoundaryLifecycle(
        IWorkspaceProvider workspace,
        IPermissionGrantStore grantStore)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _grantStore = grantStore ?? throw new ArgumentNullException(nameof(grantStore));
    }

    public void Attach()
    {
        if (_attached) return;
        _workspace.WorkspaceRootChanged += OnWorkspaceRootChanged;
        _attached = true;
    }

    private void OnWorkspaceRootChanged(object? sender, WorkspaceChangedEventArgs e)
    {
        _grantStore.ClearAll();
    }

    public void Dispose()
    {
        if (!_attached) return;
        _workspace.WorkspaceRootChanged -= OnWorkspaceRootChanged;
        _attached = false;
    }
}
