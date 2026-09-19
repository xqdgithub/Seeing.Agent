using Seeing.Agent.Abstractions.Permissions;

namespace Seeing.Agent.WebUI.Models;

/// <summary>
/// 权限内联卡片投影模型（UI 只读视图，非权威）。
/// <para>
/// 权威在途状态在 <see cref="IPermissionRequestManager"/>；本模型由
/// <c>PermissionInbox</c> 按 <c>RequestId</c> 聚合、按 <c>CallId</c> 关联工具卡片。
/// 渲染只读、无副作用（timeline §5.1）。
/// </para>
/// </summary>
public sealed class PermissionCardModel
{
    /// <summary>在途请求唯一 ID（@key 关联键）。</summary>
    public required string RequestId { get; init; }

    /// <summary>所属会话 ID（决策回传时用作 expectedSessionId 校验）。</summary>
    public string SessionId { get; init; } = string.Empty;

    /// <summary>工具调用 ID：内联卡片关联键。</summary>
    public string? CallId { get; init; }

    /// <summary>所属 Loop ID。</summary>
    public string? LoopId { get; init; }

    /// <summary>权限种类（如 filesystem.read / tool.execute）。</summary>
    public string PermissionKind { get; init; } = string.Empty;

    /// <summary>资源标识（文件路径 / 工具名等）。</summary>
    public string? Resource { get; init; }

    /// <summary>展示参数。</summary>
    public object? Arguments { get; init; }

    /// <summary>提示信息。</summary>
    public string? Message { get; init; }

    /// <summary>风险等级（low / medium / high / critical）。</summary>
    public string RiskLevel { get; init; } = "medium";

    /// <summary>允许的动作集合（由执行级授权器按 kind 注入）。</summary>
    public IReadOnlyList<PermissionGrantScope> AllowedScopes { get; init; } = Array.Empty<PermissionGrantScope>();

    /// <summary>请求创建时间。</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>是否仍在途（决议后为 false）。</summary>
    public bool IsPending { get; internal set; } = true;

    /// <summary>决议结果（Allow / Deny）；未决为 null。</summary>
    public PermissionEffect? Decision { get; internal set; }

    /// <summary>决议作用域。</summary>
    public PermissionGrantScope Scope { get; internal set; } = PermissionGrantScope.Once;

    /// <summary>决议来源。</summary>
    public PermissionResolvedBy? ResolvedBy { get; internal set; }

    /// <summary>决议原因（可选）。</summary>
    public string? Reason { get; internal set; }

    /// <summary>是否为文件系统类权限（决定是否呈现「允许此目录」）。</summary>
    public bool IsFileSystem =>
        PermissionKind.StartsWith("filesystem", StringComparison.OrdinalIgnoreCase);

    /// <summary>该动作作用域是否在允许集合内。</summary>
    public bool Allows(PermissionGrantScope scope) => AllowedScopes.Contains(scope);

    public bool CanAllowOnce => IsPending && Allows(PermissionGrantScope.Once);

    public bool CanAllowSession => IsPending && Allows(PermissionGrantScope.Session);

    /// <summary>「允许此目录」仅对 filesystem.* 且作用域集合包含 SessionDirectory 时呈现。</summary>
    public bool CanAllowSessionDirectory =>
        IsPending && IsFileSystem && Allows(PermissionGrantScope.SessionDirectory);

    public bool CanDenyOnce => IsPending;

    public bool CanDenySession => IsPending && Allows(PermissionGrantScope.Session);
}
