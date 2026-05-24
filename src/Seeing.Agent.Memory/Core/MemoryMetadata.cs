namespace Seeing.Agent.Memory.Core;

public record MemoryMetadata(
    string SessionId,
    string AgentId,
    string Source,
    IReadOnlyList<string> Tags,
    double Confidence,
    double Importance,
    int AccessCount = 0
);
