using Seeing.Agent.Abstractions.Permissions;

namespace Seeing.Agent.Core.Permission;

/// <summary>
/// 权限 kind 字符串（<c>tool.execute</c> / <c>filesystem.*</c> / <c>shell.*</c> / <c>network.*</c> / <c>mcp.*</c>）
/// 到 <see cref="PermissionKind"/> 的纯函数映射。
/// </summary>
public static class PermissionKindMapper
{
    /// <summary>映射权限 kind 字符串；未知或空值回退为 <see cref="PermissionKind.Tool"/>。</summary>
    public static PermissionKind Map(string? permissionKind)
    {
        if (string.IsNullOrWhiteSpace(permissionKind))
            return PermissionKind.Tool;

        if (permissionKind.Equals("tool.execute", StringComparison.OrdinalIgnoreCase))
            return PermissionKind.Tool;

        if (permissionKind.StartsWith("filesystem.", StringComparison.OrdinalIgnoreCase))
            return PermissionKind.File;

        if (permissionKind.StartsWith("shell.", StringComparison.OrdinalIgnoreCase))
            return PermissionKind.Shell;

        if (permissionKind.StartsWith("network.", StringComparison.OrdinalIgnoreCase))
            return PermissionKind.Network;

        if (permissionKind.StartsWith("mcp.", StringComparison.OrdinalIgnoreCase))
            return PermissionKind.McpTool;

        return PermissionKind.Tool;
    }
}
