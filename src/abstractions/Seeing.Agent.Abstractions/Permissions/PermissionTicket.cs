namespace Seeing.Agent.Abstractions.Permissions;

public readonly record struct PermissionTicket(string RequestId, string SessionId);
