namespace Seeing.Agent.Memory.Core;

public record MemoryFilter(
    MemoryType? Type = null,
    string? SessionId = null,
    string? AgentId = null,
    string? Source = null,
    IReadOnlyList<string>? Tags = null,
    DateTimeOffset? ValidAtFrom = null,
    DateTimeOffset? ValidAtTo = null,
    DateTimeOffset? InvalidAtFrom = null,
    DateTimeOffset? InvalidAtTo = null
);
