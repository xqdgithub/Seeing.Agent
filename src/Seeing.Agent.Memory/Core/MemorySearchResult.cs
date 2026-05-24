namespace Seeing.Agent.Memory.Core;

public record MemorySearchResult(
    IReadOnlyList<MemoryEntry> Entries,
    int TotalCount
);
