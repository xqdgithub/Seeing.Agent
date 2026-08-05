namespace Seeing.Agent.Memory.Core.Models;

/// <summary>
/// 一次 flush 产出的批量候选，由后台 Worker 做一次 LLM 抽取。
/// </summary>
public sealed record MemoryBatch(
    string Id,
    string SessionId,
    IReadOnlyList<MemoryCandidate> Candidates,
    DateTimeOffset CreatedAt);
