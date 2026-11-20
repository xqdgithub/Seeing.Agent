using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Directories;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Configuration;
using Seeing.Agent.Paths;
using Seeing.Session.Core;

namespace Seeing.Agent.Core.Directories;

/// <summary>
/// 会话目录判定的唯一权威：根 = 项目根 + 会话协同目录；授权 = 根 ∪ 临时白名单。
/// <para>
/// <see cref="IsAuthorized"/> 与 <see cref="IsTrusted"/> 的语义差别是<b>安全边界</b>：
/// 白名单只服务工具读写，绝不泄漏给附件准入与工作台展示。
/// </para>
/// <para>
/// <b>只读内存会话</b>：本类是同步端口，冷会话（尚未加载）在这里看不到协同目录。
/// 需要正确结果的调用方（门闸 / 文件工作台）必须先 <c>ISessionManager.TryGetOrLoadAsync</c>
/// 预热会话——与 spec §5.9「冷会话」一致；禁止在这里同步等待存储（会死锁并把 IO 拖进判定热路径）。
/// </para>
/// </summary>
public sealed class SessionRootSet : ISessionRootSet
{
    private readonly ISessionManager _sessions;
    private readonly IWorkspaceProvider _workspace;
    private readonly IPermissionGrantStore _grants;
    private readonly ILogger<SessionRootSet> _logger;

    /// <summary>注入会话管理器、工作区提供者、临时授权存储与日志。</summary>
    public SessionRootSet(
        ISessionManager sessions,
        IWorkspaceProvider workspace,
        IPermissionGrantStore grants,
        ILogger<SessionRootSet> logger)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _grants = grants ?? throw new ArgumentNullException(nameof(grants));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public IReadOnlyList<SessionRoot> Roots(string sessionId)
    {
        var roots = new List<SessionRoot>();

        var projectRoot = TryGetProjectRoot();
        if (projectRoot is not null)
        {
            roots.Add(new SessionRoot(
                projectRoot,
                SessionRootKind.Project,
                Label: null,
                Exists: SafeExists(projectRoot)));
        }

        foreach (var directory in ReadCollaborations(sessionId))
        {
            roots.Add(new SessionRoot(
                directory.Path,
                SessionRootKind.Collaboration,
                directory.Note,
                SafeExists(directory.Path)));
        }

        return roots;
    }

    /// <inheritdoc />
    public bool IsAuthorized(string sessionId, string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
            return false;

        // 根优先：授权根命中时无需看白名单（也是最快的路径）
        if (IsTrusted(sessionId, absolutePath))
            return true;

        if (string.IsNullOrEmpty(sessionId))
            return false;

        return _grants.ContainsSessionPath(sessionId, absolutePath);
    }

    /// <inheritdoc />
    public bool IsTrusted(string sessionId, string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
            return false;

        var projectRoot = TryGetProjectRoot();
        if (projectRoot is not null && PathBoundary.IsWithin(absolutePath, projectRoot))
            return true;

        foreach (var directory in ReadCollaborations(sessionId))
        {
            if (PathBoundary.IsWithin(absolutePath, directory.Path))
                return true;
        }

        return false;
    }

    /// <summary>项目根：未决议工作区不是"根是空的"，而是"没有根"（只保留协同目录判定）。</summary>
    private string? TryGetProjectRoot()
    {
        try
        {
            var root = _workspace.GetProjectRoot();
            return string.IsNullOrWhiteSpace(root) ? null : root;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogDebug(ex, "工作区尚未决议：本次判定只包含会话协同目录。");
            return null;
        }
    }

    /// <summary>读取内存会话的协同目录（空/冷会话返回空集合，不触发加载）。</summary>
    private IReadOnlyList<CollaborationDirectory> ReadCollaborations(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return Array.Empty<CollaborationDirectory>();

        var directories = _sessions.Get(sessionId)?.CollaborationDirectories;
        if (directories is null || directories.Count == 0)
            return Array.Empty<CollaborationDirectory>();

        var projectRoot = TryGetProjectRoot();
        var usable = new List<CollaborationDirectory>(directories.Count);
        foreach (var directory in directories)
        {
            if (directory is null || string.IsNullOrWhiteSpace(directory.Path))
                continue;

            var path = directory.Path;
            if (!Path.IsPathFullyQualified(path))
            {
                if (projectRoot is null)
                    continue;

                try
                {
                    path = Path.GetFullPath(path, projectRoot);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    _logger.LogWarning(ex, "协同目录路径无法规范化，判定时已跳过：{Path}", directory.Path);
                    continue;
                }
            }

            usable.Add(new CollaborationDirectory(path, directory.Note));
        }

        return usable;
    }

    private static bool SafeExists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // 不可达（网络盘 / 无权限）按"当前不可用"处理：UI 灰显，判定仍按路径归属
            return false;
        }
    }
}
