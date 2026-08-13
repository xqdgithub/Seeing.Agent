using System.Collections.Concurrent;
using Seeing.Agent.Tools.BuiltIn.FileSystem;

namespace Seeing.Agent.Core.Permission;

/// <summary>
/// 会话级工作区白名单 - 允许 Agent 扩展可访问路径。
/// </summary>
public interface IWorkspaceWhitelist
{
    void Add(string sessionId, string directoryPath);
    /// <summary>判断 path 是否等于某白名单目录或位于其子目录内（子目录前缀匹配）</summary>
    bool Contains(string sessionId, string path);
    void ClearSession(string sessionId);
}

/// <summary>
/// 会话级工作区白名单实现 - 纯内存，按 SessionId 隔离。
/// </summary>
public sealed class SessionWorkspaceWhitelist : IWorkspaceWhitelist
{
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _store =
        new(PathComparer);

    public void Add(string sessionId, string directoryPath)
    {
        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrWhiteSpace(directoryPath)) return;

        var full = Path.GetFullPath(directoryPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var dirs = _store.GetOrAdd(sessionId, _ => new ConcurrentDictionary<string, byte>(PathComparer));
        dirs[full] = 0;
    }

    public bool Contains(string sessionId, string path)
    {
        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrWhiteSpace(path)) return false;
        if (!_store.TryGetValue(sessionId, out var dirs)) return false;

        foreach (var dir in dirs.Keys)
        {
            if (FileSystemHelper.IsPathWithinDirectory(path, dir))
                return true;
        }
        return false;
    }

    public void ClearSession(string sessionId)
    {
        if (!string.IsNullOrEmpty(sessionId))
            _store.TryRemove(sessionId, out _);
    }
}
