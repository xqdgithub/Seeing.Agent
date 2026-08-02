using System.Collections.Concurrent;

namespace Seeing.Agent.Core.Permission;

/// <summary>
/// 会话级权限记忆 — 纯内存实现，按 SessionId 隔离，支持目录前缀匹配
/// </summary>
public class SessionPermissionMemory : IPermissionMemory
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, PermissionMemoryEntry>> _store = new();

    public PermissionMemoryEntry? Match(string permissionKind, string? resource, string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId) || !_store.TryGetValue(sessionId, out var entries))
            return null;

        foreach (var (_, entry) in entries)
        {
            if (!string.Equals(entry.PermissionKind, permissionKind, StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.Equals(entry.Resource, resource, StringComparison.OrdinalIgnoreCase))
                return entry;

            // 目录前缀匹配：统一路径分隔符后比较
            if (resource != null && entry.Resource != null)
            {
                var normalizedResource = resource.Replace('\\', '/');
                var normalizedEntry = entry.Resource.Replace('\\', '/');
                if (normalizedEntry.EndsWith('/') &&
                    normalizedResource.StartsWith(normalizedEntry, StringComparison.OrdinalIgnoreCase))
                    return entry;
            }

            // 通配符匹配：仅支持 ** 后缀（递归目录匹配），且资源长度必须大于 2
            if (resource != null && entry.Resource != null &&
                entry.Resource.Length > 2 &&
                entry.Resource.EndsWith("**") &&
                resource.StartsWith(entry.Resource[..^2], StringComparison.OrdinalIgnoreCase))
                return entry;
        }

        return null;
    }

    public void Remember(string sessionId, PermissionMemoryEntry entry)
    {
        var entries = _store.GetOrAdd(sessionId, _ => new ConcurrentDictionary<string, PermissionMemoryEntry>(StringComparer.OrdinalIgnoreCase));
        entries[entry.Id] = entry;
    }

    public void Forget(string sessionId, string? resource)
    {
        if (string.IsNullOrEmpty(sessionId) || !_store.TryGetValue(sessionId, out var entries))
            return;

        var toRemove = entries.Where(kv =>
            resource == null || string.Equals(kv.Value.Resource, resource, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key).ToList();

        foreach (var key in toRemove)
            entries.TryRemove(key, out _);
    }

    public void ClearSession(string sessionId)
    {
        _store.TryRemove(sessionId, out _);
    }
}
