namespace Seeing.Agent.Abstractions.Files;

/// <summary>写入结果。Conflict=true 表示 expectedRevision 不匹配、未写入。</summary>
public sealed record FileWriteResult(
    FileResourceId Id,
    FileRevision? Revision,
    bool Conflict,
    FileRevision? CurrentRevision,
    string? Diff);
