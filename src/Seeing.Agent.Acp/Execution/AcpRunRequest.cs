using Acp.Types;
using Seeing.Agent.Core.Models;

namespace Seeing.Agent.Acp.Execution;

/// <summary>
/// ACP 执行请求。
/// </summary>
public sealed record AcpRunRequest
{
    public required string Scope { get; init; }

    public required string ScopeKey { get; init; }

    public required string BackendId { get; init; }

    public required string SeeingSessionId { get; init; }

    public string? LoopId { get; init; }

    public required IEnumerable<ContentBlock> Prompt { get; init; }

    public required string WorkingDirectory { get; init; }

    public AgentContext? ParentContext { get; init; }
}
