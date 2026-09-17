using Seeing.Agent.Acp.Execution;

namespace Seeing.Agent.Acp.Permission;

/// <summary>
/// ACP 权限回调上下文（AsyncLocal 栈帧）。
/// </summary>
public sealed record AcpPermissionContext
{
    public required string SeeingSessionId { get; init; }

    public string? LoopId { get; init; }

    /// <summary>发起权限请求的 ACP Agent 名（acp-{backendId}），用于权限规则/审计归属。</summary>
    public string? AgentName { get; init; }

    public required string AcpSessionId { get; init; }

    public IAcpUpdateSink? UpdateSink { get; init; }
}
