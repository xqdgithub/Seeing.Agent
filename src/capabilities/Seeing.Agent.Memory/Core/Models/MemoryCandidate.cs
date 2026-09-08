namespace Seeing.Agent.Memory.Core.Models;

public enum MemorySource
{
    Chat,
    Tool
}

public record MemoryCandidate(
    string Id,
    string SessionId,
    string? AgentId,
    MemorySource Source,
    string? ToolId,
    string Snippet,
    DateTimeOffset CreatedAt);
