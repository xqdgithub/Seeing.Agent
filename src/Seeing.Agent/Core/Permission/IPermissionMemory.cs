namespace Seeing.Agent.Core.Permission;

/// <summary>
/// 权限记忆条目
/// </summary>
public class PermissionMemoryEntry
{
    /// <summary>唯一标识</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>权限类别</summary>
    public string PermissionKind { get; set; } = string.Empty;

    /// <summary>目标资源（工具名 / 文件路径 / 目录路径）</summary>
    public string? Resource { get; set; }

    /// <summary>决策</summary>
    public PermissionMemoryAction Action { get; set; }

    /// <summary>创建时间</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum PermissionMemoryAction
{
    Allow = 0,
    Deny = 1,
}

/// <summary>
/// 会话级权限记忆接口 — 纯内存，无持久化
/// </summary>
public interface IPermissionMemory
{
    /// <summary>查询记忆：按 kind + resource 匹配（支持目录前缀）</summary>
    PermissionMemoryEntry? Match(string permissionKind, string? resource, string sessionId);

    /// <summary>写入记忆</summary>
    void Remember(string sessionId, PermissionMemoryEntry entry);

    /// <summary>
    /// 遗忘指定资源的记忆。当 <paramref name="resource"/> 为 null 时，删除该会话中所有记忆。
    /// 如需精确删除，请传递具体的资源标识。
    /// </summary>
    void Forget(string sessionId, string? resource);

    /// <summary>清除整个会话的记忆</summary>
    void ClearSession(string sessionId);
}
