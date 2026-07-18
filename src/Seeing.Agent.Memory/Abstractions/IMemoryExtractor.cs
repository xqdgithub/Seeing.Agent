using Seeing.Agent.Memory.Core.Models;

namespace Seeing.Agent.Memory.Abstractions;

public interface IMemoryExtractor
{
    Task<ExtractionResult?> ExtractAsync(MemoryCandidate candidate, CancellationToken ct = default);
}

public interface IMemoryPipeline
{
    Task<PipelineResult> ProcessAsync(MemoryCandidate candidate, CancellationToken ct = default);
}
