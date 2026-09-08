using System.Collections.Concurrent;
using Seeing.Agent.Abstractions.Permissions;

namespace Seeing.Agent.Core.Permission;

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

        var full = Path.GetFullPath(directoryPath);
        var isRoot = full.Length == 1 && (full[0] == '/' || full[0] == '\\');
        var isDriveRoot = full.Length >= 2 && full[1] == ':' && full.Length <= 3;
        if (!isRoot && !isDriveRoot)
            full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var dirs = _store.GetOrAdd(sessionId, _ => new ConcurrentDictionary<string, byte>(PathComparer));
        dirs[full] = 0;
    }

    public bool Contains(string sessionId, string path)
    {
        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrWhiteSpace(path)) return false;
        if (!_store.TryGetValue(sessionId, out var dirs)) return false;

        foreach (var dir in dirs.Keys)
        {
            if (PathSafety.IsPathWithinDirectory(path, dir))
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
