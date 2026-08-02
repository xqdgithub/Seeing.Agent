namespace Seeing.Agent.Core.Permission;

/// <summary>
/// 权限通道统一返回结果
/// </summary>
public class PermissionChannelResult
{
    /// <summary>最终决策</summary>
    public PermissionChannelAction Action { get; init; }

    /// <summary>需要记忆的资源标识（null=不记住）</summary>
    public string? ResourceToRemember { get; init; }

    /// <summary>拒绝原因</summary>
    public string? Reason { get; init; }

    /// <summary>匹配的记忆条目（内部使用）</summary>
    internal PermissionMemoryEntry? MatchedMemory { get; init; }

    public static PermissionChannelResult Allowed(string? resourceToRemember = null)
        => new() { Action = PermissionChannelAction.Allow, ResourceToRemember = resourceToRemember };

    public static PermissionChannelResult Denied(string reason, string? resourceToRemember = null)
        => new() { Action = PermissionChannelAction.Deny, Reason = reason, ResourceToRemember = resourceToRemember };
}

public enum PermissionChannelAction
{
    Allow = 0,
    Deny = 1,
}
