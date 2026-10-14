namespace Seeing.Agent.Abstractions.Files;

/// <summary>结构化读取结果。</summary>
public sealed record FileReadResult(
    FileResourceId Id,
    FileRevision? Revision,
    string? Content,
    FileContentKind Kind,
    string MimeType,
    string? Encoding,
    bool IsEditable,
    long Size,
    int TotalLines,
    bool IsTruncated,
    bool IsSampled,
    FileReadMode Mode);
