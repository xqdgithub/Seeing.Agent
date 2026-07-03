using Seeing.Agent.Llm;

namespace Seeing.Agent.Acp.Execution;

/// <summary>
/// ACP 执行结果。
/// </summary>
public sealed record AcpRunResult
{
    public required string Text { get; init; }

    public string? StopReason { get; init; }

    public TokenUsage? Usage { get; init; }

    public bool Success { get; init; } = true;

    public string? Error { get; init; }
}
