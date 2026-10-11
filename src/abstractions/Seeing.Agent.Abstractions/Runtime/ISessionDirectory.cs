using Seeing.Session.Core;

namespace Seeing.Agent.Abstractions.Runtime;

public interface ISessionDirectory
{
    Task<SessionData?> GetAsync(string sessionId, CancellationToken ct = default);
    Task<SessionData> EnsureAsync(string sessionId, CancellationToken ct = default);
    Task<IReadOnlyList<SessionData>> ListAsync(CancellationToken ct = default);
    Task<SessionData> CreateAsync(string? title = null, string? agentId = null, string? workingDirectory = null, CancellationToken ct = default);
    Task DeleteAsync(string sessionId, CancellationToken ct = default);
    Task RenameAsync(string sessionId, string newTitle, CancellationToken ct = default);
    Task<SessionData> BranchAsync(string sessionId, string? title = null, CancellationToken ct = default);
}
