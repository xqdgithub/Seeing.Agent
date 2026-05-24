namespace Seeing.Agent.Memory.Core;

// Memory entry entity with temporal validity fields (valid_at / invalid_at)
public record MemoryEntry(
    string Id,
    MemoryType Type,
    string Content,
    MemoryMetadata Metadata,
    DateTimeOffset CreatedAt,
    DateTimeOffset ValidAt,
    DateTimeOffset? InvalidAt
);
