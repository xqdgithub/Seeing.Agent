namespace Seeing.Session.Core
{
    /// <summary>
    /// Defines lifecycle operations for a Session.
    /// </summary>
    public interface ISessionLifecycle
    {
        /// <summary>
        /// Begin a new session with optional title and agentId.
        /// </summary>
        /// <param name="title">Optional session title.</param>
        /// <param name="agentId">Optional agent identifier.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the created SessionData.</returns>
        Task<SessionData> BeginSessionAsync(string? title = null, string? agentId = null);

        /// <summary>
        /// End an existing session by its identifier.
        /// </summary>
        /// <param name="sessionId">The id of the session to end.</param>
        /// <returns>A Task that completes when the session is ended.</returns>
        Task EndSessionAsync(string sessionId);

        /// <summary>
        /// Clone an existing session into a new one, optionally with a new title.
        /// </summary>
        /// <param name="sourceId">Source session id to clone from.</param>
        /// <param name="newTitle">Optional new title for the cloned session.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the cloned SessionData.</returns>
        Task<SessionData> CloneSessionAsync(string sourceId, string? newTitle = null);
    }
}
