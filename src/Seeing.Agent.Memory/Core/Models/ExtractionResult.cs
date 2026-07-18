namespace Seeing.Agent.Memory.Core.Models;

public record ExtractionResult(
    string Title,
    string Content,
    double Importance,
    IReadOnlyList<string> Tags,
    string Kind);

public record PipelineResult(bool Stored, string? DailyPath, string? Reason);
