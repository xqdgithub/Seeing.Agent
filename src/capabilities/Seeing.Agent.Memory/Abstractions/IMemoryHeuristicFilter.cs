namespace Seeing.Agent.Memory.Abstractions;

public record FilterDecision(bool Accepted, string? Reason);

public interface IMemoryHeuristicFilter
{
    FilterDecision Evaluate(Core.Models.MemoryCandidate candidate);
}
