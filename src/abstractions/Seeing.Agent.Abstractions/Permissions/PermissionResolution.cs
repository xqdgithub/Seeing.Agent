namespace Seeing.Agent.Abstractions.Permissions;

public sealed record PermissionResolution
{
    public required string RequestId { get; init; }
    public required string SessionId { get; init; }
    public string? CallId { get; init; }
    public required PermissionEffect Decision { get; init; }          // Allow | Deny
    public PermissionGrantScope Scope { get; init; } = PermissionGrantScope.Once;
    public PermissionResolvedBy ResolvedBy { get; init; } = PermissionResolvedBy.User;
    public string? Reason { get; init; }
    public DateTimeOffset ResolvedAt { get; init; } = DateTimeOffset.UtcNow;
}
