using Seeing.Agent.Acp.Execution;

namespace Seeing.Agent.Acp.Permission;

/// <summary>
/// ACP 权限回调上下文（AsyncLocal 栈帧）。
/// </summary>
public sealed record AcpPermissionContext
{
    public required string SeeingSessionId { get; init; }

    public string? LoopId { get; init; }

    public required string AcpSessionId { get; init; }

    public IAcpUpdateSink? UpdateSink { get; init; }

    public Seeing.Agent.Core.Interfaces.IPermissionChannel? PermissionChannel { get; init; }
}
