using Seeing.Agent.Memory.Core.Models;

namespace Seeing.Agent.Memory.Abstractions;

public interface IMemoryEvolutionService
{
    Task EvolveSessionAsync(string sessionId, CancellationToken ct = default);
}

public interface IMemoryRecallService
{
    Task<IReadOnlyList<SearchHit>> RecallAsync(string query, CancellationToken ct = default);
}
