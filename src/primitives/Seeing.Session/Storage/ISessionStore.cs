using Seeing.Session.Core;

namespace Seeing.Session.Storage
{
    // Repository-like interface for persisting and querying SessionData
    public interface ISessionStore
    {
        // Persist a single session
        Task SaveAsync(SessionData data);

        // Load a single session by its Id
        Task<SessionData?> LoadAsync(string sessionId);

        // Delete a session by its Id
        Task DeleteAsync(string sessionId);

        // Enumerate all sessions asynchronously
        Task<IAsyncEnumerable<SessionData>> ListAsync();

        // Query sessions by partition and agent, returning a filtered async enumerable
        Task<IAsyncEnumerable<SessionData>> QueryAsync(string partitionId, string agentId);

        // Batch operations
        Task SaveAllAsync(IEnumerable<SessionData> data);
        Task<IAsyncEnumerable<SessionData>> LoadAllAsync();
    }
}
