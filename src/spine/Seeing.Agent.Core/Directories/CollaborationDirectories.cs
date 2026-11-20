using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Directories;
using Seeing.Agent.Configuration;
using Seeing.Agent.Paths;
using Seeing.Session.Core;
namespace Seeing.Agent.Core.Directories;

/// <summary>
/// 会话协同目录的唯一写入口：读写 <see cref="SessionData.CollaborationDirectories"/>，负责绝对化、
/// 规范化与去重，并在变更后持久化会话。
/// <para>
/// <b>失效不删除</b>：目录不存在不是"移除"的理由——它是用户登记的配置，UI 灰显、执行期跳过即可。
/// 只有显式 <see cref="RemoveAsync"/> 才会把条目从会话里去掉。
/// </para>
/// <para>
/// <b>绝对化基准</b>：相对路径按项目根（<c>IWorkspaceProvider.GetProjectRoot()</c>）归一化，
/// 绝不用进程 CWD——那会让同一登记在不同宿主里落到不同目录。
/// </para>
/// </summary>
public sealed class CollaborationDirectories : ICollaborationDirectories
{
    private readonly ISessionManager _sessions;
    private readonly IWorkspaceProvider _workspace;
    private readonly ISessionEventPublisher _events;
    private readonly ILogger<CollaborationDirectories> _logger;

    /// <summary>注入会话管理器、工作区提供者、事件发布器与日志。</summary>
    public CollaborationDirectories(
        ISessionManager sessions,
        IWorkspaceProvider workspace,
        ISessionEventPublisher events,
        ILogger<CollaborationDirectories> logger)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CollaborationDirectory>> GetAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(sessionId))
            return Array.Empty<CollaborationDirectory>();

        var session = _sessions.Get(sessionId)
            ?? await _sessions.TryGetOrLoadAsync(sessionId, ct).ConfigureAwait(false);

        if (session is null)
        {
            _logger.LogDebug("读取协同目录时会话不存在：SessionId={SessionId}", sessionId);
            return Array.Empty<CollaborationDirectory>();
        }

        // 返回副本：调用方不得就地改写会话里的列表
        return Normalize(session.CollaborationDirectories);
    }

    /// <inheritdoc />
    public async Task AddAsync(
        string sessionId,
        string absolutePath,
        string? note = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
            throw new ArgumentException("协同目录路径不能为空。", nameof(absolutePath));

        var normalized = NormalizePath(absolutePath);

        await MutateAsync(sessionId, directories =>
        {
            var index = IndexOf(directories, normalized);
            if (index >= 0)
            {
                // 已登记：仅在备注确实变化时更新（保持「添加」幂等）
                var existing = directories[index];
                if (note is not null && !string.Equals(existing.Note, note, StringComparison.Ordinal))
                    directories[index] = new CollaborationDirectory(existing.Path, note);
                return;
            }

            directories.Add(new CollaborationDirectory(normalized, note));
        }, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "已添加协同目录：SessionId={SessionId} Path={Path}", sessionId, normalized);
    }

    /// <inheritdoc />
    public async Task RemoveAsync(
        string sessionId,
        string absolutePath,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
            throw new ArgumentException("协同目录路径不能为空。", nameof(absolutePath));

        var normalized = NormalizePath(absolutePath);
        var removed = false;

        await MutateAsync(sessionId, directories =>
        {
            var index = IndexOf(directories, normalized);
            if (index < 0)
                return;

            directories.RemoveAt(index);
            removed = true;
        }, ct).ConfigureAwait(false);

        if (removed)
            _logger.LogInformation(
                "已移除协同目录：SessionId={SessionId} Path={Path}", sessionId, normalized);
    }

    /// <summary>加载会话 → 在规范化副本上变更 → 写回并持久化。</summary>
    private async Task MutateAsync(
        string sessionId,
        Action<List<CollaborationDirectory>> mutate,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(sessionId))
            throw new ArgumentException("会话 ID 不能为空。", nameof(sessionId));

        var cached = _sessions.Get(sessionId);
        if (cached is null && await _sessions.TryGetOrLoadAsync(sessionId, ct).ConfigureAwait(false) is null)
            throw new InvalidOperationException($"Session not found: {sessionId}");

        var session = await _sessions.UpdateSessionAsync(sessionId, data =>
        {
            var directories = Normalize(data.CollaborationDirectories).ToList();
            mutate(directories);
            data.CollaborationDirectories = directories;
        }, ct).ConfigureAwait(false);

        // 配置已落盘：通知 UI 重载文件根列表（这是唯一需要实时刷新的消费方）
        _events.Publish(new SessionEvent
        {
            SessionId = sessionId,
            Type = SessionEventType.CollaborationDirectoriesChanged,
            Data = session
        });
    }

    /// <summary>
    /// 规范化条目集合：绝对化（相对路径按项目根）、去重（按 <see cref="PathBoundary.AreSame"/>，
    /// Windows/macOS 大小写不敏感）、丢弃空路径。不检查目录是否存在。
    /// </summary>
    private List<CollaborationDirectory> Normalize(IEnumerable<CollaborationDirectory>? source)
    {
        var result = new List<CollaborationDirectory>();
        if (source is null)
            return result;

        foreach (var entry in source)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Path))
                continue;

            string normalized;
            try
            {
                normalized = NormalizePath(entry.Path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // 历史脏数据无法绝对化：跳过并在下次写入时被清除（不参与判定，也不阻断读取）
                _logger.LogWarning(ex, "协同目录路径无法规范化，已跳过：{Path}", entry.Path);
                continue;
            }

            if (IndexOf(result, normalized) >= 0)
                continue;

            result.Add(new CollaborationDirectory(normalized, entry.Note));
        }

        return result;
    }

    /// <summary>绝对化（相对路径按项目根）+ <see cref="PathBoundary.Canonicalize"/>（realpath + 去尾分隔符）。</summary>
    private string NormalizePath(string path)
    {
        var candidate = path.Trim();
        string absolute;

        if (Path.IsPathFullyQualified(candidate))
        {
            absolute = candidate;
        }
        else
        {
            string projectRoot;
            try
            {
                projectRoot = _workspace.GetProjectRoot();
            }
            catch (InvalidOperationException ex)
            {
                throw new ArgumentException(
                    $"相对路径需要先用绝对路径登记（工作区尚未选定）：{path}", nameof(path), ex);
            }

            absolute = Path.GetFullPath(candidate, projectRoot);
        }

        // 登记 / 审批展示 / 落盘 / 边界判定必须使用同一规范化结果（含符号链接真实路径）
        return PathBoundary.Canonicalize(absolute);
    }

    private static int IndexOf(IReadOnlyList<CollaborationDirectory> directories, string path)
    {
        for (var i = 0; i < directories.Count; i++)
        {
            if (PathBoundary.AreSame(directories[i].Path, path))
                return i;
        }

        return -1;
    }
}
