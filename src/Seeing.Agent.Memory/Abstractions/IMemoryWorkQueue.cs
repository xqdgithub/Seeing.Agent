using Seeing.Agent.Memory.Core.Models;

namespace Seeing.Agent.Memory.Abstractions;

public interface IMemoryWorkQueue
{
    bool TryEnqueue(MemoryCandidate candidate);
    IAsyncEnumerable<MemoryCandidate> ReadAllAsync(CancellationToken ct);
    int Count { get; }
}
