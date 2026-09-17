namespace Seeing.Agent.Abstractions.Permissions;

/// <summary>唯一授权存储（记忆 + 白名单目录面）；匹配/优先级判定在 IPermissionService。</summary>
public interface IPermissionGrantStore
{
    // 决策记忆
    void Add(string sessionId, PermissionGrant grant);

    void Remove(string sessionId, string permissionKind, string? resource);

    IReadOnlyList<PermissionGrant> Lookup(string sessionId, string permissionKind, string? resource);

    // 白名单目录面（取代 IWorkspaceWhitelist）
    void AddSessionDirectory(string sessionId, string directoryPath);

    bool ContainsSessionPath(string sessionId, string path);

    void ClearSession(string sessionId);

    void ClearAll();
}
