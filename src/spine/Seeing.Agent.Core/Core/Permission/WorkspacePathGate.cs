using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Configuration;

namespace Seeing.Agent.Core.Permission;

/// <summary>
/// 工作区路径硬边界门闸：Restrict=false 时一律放行；否则校验
/// 全局根 ∪ 会话有效根 ∪ 会话白名单。
/// </summary>
public sealed class WorkspacePathGate : IWorkspacePathGate
{
    private readonly IWorkspaceProvider _workspace;
    private readonly IPermissionGrantStore _grantStore;
    private readonly IOptionsMonitor<SeeingAgentOptions> _options;
    private readonly ISessionWorkspaceResolver? _sessionWorkspace;

    /// <summary>
    /// 构造路径门闸，注入工作区提供者、授权存储、配置监听与会话工作目录解析器。
    /// </summary>
    public WorkspacePathGate(
        IWorkspaceProvider workspace,
        IPermissionGrantStore grantStore,
        IOptionsMonitor<SeeingAgentOptions> options,
        ISessionWorkspaceResolver? sessionWorkspace = null)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _grantStore = grantStore ?? throw new ArgumentNullException(nameof(grantStore));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _sessionWorkspace = sessionWorkspace;
    }

    /// <inheritdoc />
    public string? EnsureAllowed(string sessionId, string path)
    {
        if (!_options.CurrentValue.Workspace.RestrictToWorkspace)
            return null;

        if (string.IsNullOrEmpty(sessionId))
            return "缺少会话 ID，无法在硬边界模式下访问文件。请使用 add_workspace_path 或确保会话上下文完整。";

        if (string.IsNullOrWhiteSpace(path))
            return "路径为空，无法在硬边界模式下访问。";

        try
        {
            var full = Path.GetFullPath(path);
            if (IsWithinWorkspaceCore(sessionId, full))
                return null;

            if (_grantStore.ContainsSessionPath(sessionId, full))
                return null;

            return $"路径不在工作区允许集内: {full}。请批准权限扩权，或使用 add_workspace_path 将目录加入会话白名单。";
        }
        catch (Exception ex)
        {
            return $"无法校验工作区路径边界: {ex.Message}";
        }
    }

    /// <inheritdoc />
    public string? EnsureWithinWorkspace(string sessionId, string path)
    {
        if (string.IsNullOrEmpty(sessionId))
            return "缺少会话 ID，无法校验工作区路径。";

        if (string.IsNullOrWhiteSpace(path))
            return "路径为空，无法校验工作区路径。";

        try
        {
            var full = Path.GetFullPath(path);
            return IsWithinWorkspaceCore(sessionId, full)
                ? null
                : $"路径不在会话工作区内: {full}";
        }
        catch (Exception ex)
        {
            return $"无法校验工作区路径: {ex.Message}";
        }
    }

    /// <summary>
    /// 无条件工作区判定核心（项目根 ∪ 会话自身有效根）；两个门闸共享同一路径判定。
    /// </summary>
    private bool IsWithinWorkspaceCore(string sessionId, string fullPath)
    {
        var root = _workspace.GetProjectRoot();
        if (!string.IsNullOrEmpty(root) && PathSafety.IsPathWithinDirectory(fullPath, root))
            return true;

        // 会话自身有效根（绝对化后）视为边界内；非扩权，不写白名单
        var sessionCwd = _sessionWorkspace?.ResolveWorkingDirectory(sessionId);
        return !string.IsNullOrEmpty(sessionCwd)
            && PathSafety.IsPathWithinDirectory(fullPath, sessionCwd);
    }
}
