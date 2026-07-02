namespace Seeing.Gateway.Models;

/// <summary>
/// 待处理的权限请求
/// </summary>
public record GatewayPendingPermission
{
    public required string PermissionId { get; init; }

    public required string SessionId { get; init; }

    public string? LoopId { get; init; }

    public required string PermissionKind { get; init; }

    public string? Resource { get; init; }

    public object? Arguments { get; init; }

    public string? Message { get; init; }

    public string RiskLevel { get; init; } = "medium";

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}
