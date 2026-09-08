using Seeing.Agent.Memory.Core.Models;

namespace Seeing.Agent.Memory.Abstractions;

public interface IMemoryWorkQueue
{
    bool TryEnqueue(MemoryBatch batch);
    IAsyncEnumerable<MemoryBatch> ReadAllAsync(CancellationToken ct);
}
