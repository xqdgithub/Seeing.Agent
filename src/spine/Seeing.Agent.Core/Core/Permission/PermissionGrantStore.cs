using System.Collections.Concurrent;
using Seeing.Agent.Abstractions.Permissions;

namespace Seeing.Agent.Core.Permission;

/// <summary>
/// 唯一授权存储：决策记忆 + 白名单目录面（纯数据存取；匹配/优先级判定在 <see cref="IPermissionService"/>）。
/// </summary>
public sealed class PermissionGrantStore : IPermissionGrantStore
{
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, PermissionGrant>> _grants =
        new(PathComparer);

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _directories =
        new(PathComparer);

    /// <inheritdoc />
    public void Add(string sessionId, PermissionGrant grant)
    {
        if (string.IsNullOrEmpty(sessionId) || grant is null)
            return;

        var entries = _grants.GetOrAdd(
            sessionId,
            _ => new ConcurrentDictionary<string, PermissionGrant>(StringComparer.OrdinalIgnoreCase));
        entries[BuildKey(grant.PermissionKind, grant.Resource)] = grant;
    }

    /// <inheritdoc />
    public void Remove(string sessionId, string permissionKind, string? resource)
    {
        if (string.IsNullOrEmpty(sessionId))
            return;

        if (_grants.TryGetValue(sessionId, out var entries))
            entries.TryRemove(BuildKey(permissionKind, resource), out _);
    }

    /// <inheritdoc />
    public IReadOnlyList<PermissionGrant> Lookup(string sessionId, string permissionKind, string? resource)
    {
        if (string.IsNullOrEmpty(sessionId) || !_grants.TryGetValue(sessionId, out var entries))
            return Array.Empty<PermissionGrant>();

        var result = new List<PermissionGrant>();
        foreach (var grant in entries.Values)
        {
            if (!string.Equals(grant.PermissionKind, permissionKind, StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.Equals(grant.Resource, resource, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(grant);
                continue;
            }

            if (grant.Scope == PermissionGrantScope.SessionDirectory &&
                !string.IsNullOrWhiteSpace(grant.Resource) &&
                !string.IsNullOrWhiteSpace(resource) &&
                PathSafety.IsPathWithinDirectory(resource, grant.Resource))
            {
                result.Add(grant);
            }
        }

        return result;
    }

    /// <inheritdoc />
    public void AddSessionDirectory(string sessionId, string directoryPath)
    {
        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrWhiteSpace(directoryPath))
            return;

        var full = Path.GetFullPath(directoryPath);
        var isRoot = full.Length == 1 && (full[0] == '/' || full[0] == '\\');
        var isDriveRoot = full.Length >= 2 && full[1] == ':' && full.Length <= 3;
        if (!isRoot && !isDriveRoot)
            full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var dirs = _directories.GetOrAdd(sessionId, _ => new ConcurrentDictionary<string, byte>(PathComparer));
        dirs[full] = 0;
    }

    /// <inheritdoc />
    public bool ContainsSessionPath(string sessionId, string path)
    {
        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrWhiteSpace(path))
            return false;

        if (!_directories.TryGetValue(sessionId, out var dirs))
            return false;

        foreach (var dir in dirs.Keys)
        {
            if (PathSafety.IsPathWithinDirectory(path, dir))
                return true;
        }

        return false;
    }

    /// <inheritdoc />
    public void ClearSession(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return;

        _grants.TryRemove(sessionId, out _);
        _directories.TryRemove(sessionId, out _);
    }

    /// <inheritdoc />
    public void ClearAll()
    {
        _grants.Clear();
        _directories.Clear();
    }

    private static string BuildKey(string permissionKind, string? resource) =>
        string.Concat(permissionKind, "\0", resource ?? string.Empty);
}
