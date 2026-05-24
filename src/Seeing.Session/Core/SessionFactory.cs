using System.Text.Json;

namespace Seeing.Session.Core
{
    // Factory responsible for creating, cloning and resuming sessions
    public class SessionFactory : ISessionFactory
    {
        private readonly Seeing.Session.Storage.ISessionStore _store;

        public SessionFactory(Seeing.Session.Storage.ISessionStore store)
        {
            _store = store;
        }

        public async Task<ISession> CreateAsync(string title = null, string partitionId = null, string agentId = null)
        {
            var data = new SessionData
            {
                Id = Guid.NewGuid().ToString(),
                Title = title,
                PartitionId = partitionId,
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now,
                Agent = new AgentMetadata
                {
                    AgentId = agentId
                }
            };
            // In a real implementation, you might persist initialization here.
            return new Session(data, _store);
        }

        public async Task<ISession> CloneAsync(string sourceSessionId, string newSessionId)
        {
            // Load source data from store and perform a deep copy.
            var source = await _store.LoadAsync(sourceSessionId);
            if (source == null)
            {
                throw new InvalidOperationException($"Source session '{sourceSessionId}' not found.");
            }

            // Deep copy via JSON serialize/deserialize to ensure dictionary deep copy
            var json = JsonSerializer.Serialize(source);
            var copy = JsonSerializer.Deserialize<SessionData>(json)!;

            copy.Id = newSessionId;
            var now = DateTime.Now;
            copy.CreatedAt = now;
            copy.UpdatedAt = now;
            // Preserve PartitionId and AgentId/AgentName/Role from the source
            // (they are already copied by the deserialization)

            return new Session(copy, _store);
        }

        public async Task<ISession> ResumeAsync(string sessionId)
        {
            var data = await _store.LoadAsync(sessionId);
            if (data == null)
            {
                throw new InvalidOperationException($"Session '{sessionId}' not found.");
            }
            return new Session(data, _store);
        }
    }
}
