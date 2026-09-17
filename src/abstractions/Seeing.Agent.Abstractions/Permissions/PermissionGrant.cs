namespace Seeing.Agent.Abstractions.Permissions;

public sealed record PermissionGrant(string PermissionKind, string? Resource,
                                     PermissionGrantScope Scope, PermissionEffect Effect);
