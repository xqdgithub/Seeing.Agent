using System.Collections.Concurrent;

namespace Seeing.Gateway.WeCom;

/// <summary>
/// 模板卡片 task_id 与 Gateway 权限请求的映射
/// </summary>
public sealed class WeComPermissionState
{
    private readonly ConcurrentDictionary<string, PendingPermissionCard> _byTaskId = new();
    private readonly ConcurrentDictionary<string, string> _permissionToTaskId = new();

    public void Register(string taskId, PendingPermissionCard entry)
    {
        _byTaskId[taskId] = entry;
        _permissionToTaskId[entry.PermissionId] = taskId;
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
}
