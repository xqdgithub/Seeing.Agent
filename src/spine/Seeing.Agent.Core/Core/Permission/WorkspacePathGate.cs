using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Directories;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Configuration;

namespace Seeing.Agent.Core.Permission;

/// <summary>
/// 工作区路径硬边界门闸：Restrict=false 时一律放行；否则委托 <see cref="ISessionRootSet"/>。
/// <para>本类只保留开关与错误文案，判定权威在 <see cref="ISessionRootSet"/>。</para>
/// </summary>
public sealed class WorkspacePathGate : IWorkspacePathGate
{
    private readonly ISessionRootSet _rootSet;
    private readonly IOptionsMonitor<SeeingAgentOptions> _options;

    /// <summary>构造路径门闸，注入目录判定权威与配置监听。</summary>
    public WorkspacePathGate(
        ISessionRootSet rootSet,
        IOptionsMonitor<SeeingAgentOptions> options)
    {
        _rootSet = rootSet ?? throw new ArgumentNullException(nameof(rootSet));
        _options = options ?? throw new ArgumentNullException(nameof(options));
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
            var full = Path.IsPathRooted(path) ? path : Path.GetFullPath(path);
            return _rootSet.IsAuthorized(sessionId, full)
                ? null
                : $"路径不在工作区允许集内: {full}。请批准权限扩权，或使用 add_workspace_path 将目录加入会话白名单。";
        }
        catch (Exception ex)
        {
            return $"无法校验工作区路径边界: {ex.Message}";
        }
    }

    /// <inheritdoc />
    public string? ResolveAllowed(
        string sessionId, string path, string? baseDirectory, out string fullPath)
    {
        fullPath = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
            return "路径为空，无法解析。";

        if (string.IsNullOrWhiteSpace(baseDirectory))
            return "缺少执行工作目录，无法解析相对路径。";

        try
        {
            // 绝对路径保持原样（POSIX 风格路径在 Windows 上被 GetFullPath 改写会破坏语义）；
            // 相对路径才按执行 cwd 解析。
            fullPath = Path.IsPathRooted(path)
                ? path
                : Path.GetFullPath(path, baseDirectory);
        }
        catch (Exception ex)
        {
            return $"无法解析路径: {ex.Message}";
        }

        return EnsureAllowed(sessionId ?? string.Empty, fullPath);
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
            var full = Path.IsPathRooted(path) ? path : Path.GetFullPath(path);
            return _rootSet.IsTrusted(sessionId, full)
                ? null
                : $"路径不在会话工作区内: {full}";
        }
        catch (Exception ex)
        {
            return $"无法校验工作区路径: {ex.Message}";
        }
    }
}
