namespace Seeing.Session.Core
{
    // Factory responsible for creating, cloning and resuming sessions
    public interface ISessionFactory
    {
        // Creates a new session with optional title, partition and agent identifiers
        Task<ISession> CreateAsync(string title = null, string partitionId = null, string agentId = null);

        // Clones an existing session (sourceSessionId) into a new session (newSessionId)
        Task<ISession> CloneAsync(string sourceSessionId, string newSessionId);

        // Resumes an existing session by its id
        Task<ISession> ResumeAsync(string sessionId);
    }
}
