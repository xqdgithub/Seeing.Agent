using System.Collections.Concurrent;

namespace Seeing.Gateway.WeCom;

/// <summary>
/// 模板卡片 task_id 与 Gateway 权限请求的映射
/// </summary>
public sealed class WeComPermissionState
{
    private readonly ConcurrentDictionary<string, PendingPermissionCard> _byTaskId = new();
    private readonly ConcurrentDictionary<string, string> _permissionToTaskId = new();

    public void Register(PendingPermissionCard entry, string? taskId = null)
    {
        var storageKey = !string.IsNullOrWhiteSpace(taskId)
            ? taskId
            : $"pending_{entry.PermissionId}";

        var stored = new PendingPermissionCard
        {
            SessionId = entry.SessionId,
            PermissionId = entry.PermissionId,
            Resource = entry.Resource,
            PermissionKind = entry.PermissionKind,
            ExpiresAt = entry.ExpiresAt,
            RegisteredAt = entry.RegisteredAt == default ? DateTimeOffset.UtcNow : entry.RegisteredAt,
            TaskId = taskId
        };

        _byTaskId[storageKey] = stored;
        _permissionToTaskId[entry.PermissionId] = storageKey;
    }

    public bool TryGetByTaskId(string taskId, out PendingPermissionCard entry)
        => _byTaskId.TryGetValue(taskId, out entry!);

    public bool TryRemoveByTaskId(string taskId, out PendingPermissionCard entry)
    {
        if (!_byTaskId.TryRemove(taskId, out entry!))
            return false;

        _permissionToTaskId.TryRemove(entry.PermissionId, out _);
        return true;
    }

    public bool TryRemoveByPermissionId(string permissionId, out PendingPermissionCard entry)
    {
        entry = null!;
        if (!_permissionToTaskId.TryRemove(permissionId, out var taskId))
            return false;

        return _byTaskId.TryRemove(taskId, out entry!);
    }

    public bool TryGetLatestPendingForSession(string sessionId, out PendingPermissionCard entry)
    {
        entry = null!;
        PendingPermissionCard? latest = null;

        foreach (var pending in _byTaskId.Values)
        {
            if (!string.Equals(pending.SessionId, sessionId, StringComparison.Ordinal))
                continue;

            if (pending.ExpiresAt <= DateTimeOffset.UtcNow)
                continue;

            if (latest == null || pending.RegisteredAt > latest.RegisteredAt)
                latest = pending;
        }

        if (latest == null)
            return false;

        entry = latest;
        return true;
    }

    public void CleanupExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (taskId, entry) in _byTaskId)
        {
            if (entry.ExpiresAt <= now)
            {
                _byTaskId.TryRemove(taskId, out _);
                _permissionToTaskId.TryRemove(entry.PermissionId, out _);
            }
        }
    }
}

public sealed class PendingPermissionCard
{
    public required string SessionId { get; init; }

    public required string PermissionId { get; init; }

    public required string Resource { get; init; }

    public required string PermissionKind { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    public DateTimeOffset RegisteredAt { get; init; } = DateTimeOffset.UtcNow;

    public string? TaskId { get; init; }
}
