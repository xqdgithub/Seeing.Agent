using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Configuration;

namespace Seeing.Agent.Core.Permission;

/// <summary>
/// 工作区根变更时清空全部话白名单与权限记忆，防止权限漂移。
/// </summary>
public sealed class WorkspaceBoundaryLifecycle : IDisposable
{
    private readonly IWorkspaceProvider _workspace;
    private readonly IWorkspaceWhitelist _whitelist;
    private readonly IPermissionMemory _memory;
    private bool _attached;

    public WorkspaceBoundaryLifecycle(
        IWorkspaceProvider workspace,
        IWorkspaceWhitelist whitelist,
        IPermissionMemory memory)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _whitelist = whitelist ?? throw new ArgumentNullException(nameof(whitelist));
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
    }

    public void Attach()
    {
        if (_attached) return;
        _workspace.WorkspaceRootChanged += OnWorkspaceRootChanged;
        _attached = true;
    }

    private void OnWorkspaceRootChanged(object? sender, WorkspaceChangedEventArgs e)
    {
        _whitelist.ClearAll();
        _memory.ClearAll();
    }

    public void Dispose()
    {
        if (!_attached) return;
        _workspace.WorkspaceRootChanged -= OnWorkspaceRootChanged;
        _attached = false;
    }
}
