using Microsoft.Extensions.Logging;
using Seeing.Agent.Acp.Configuration;
using Seeing.Session.Core;

namespace Seeing.Agent.Acp.Session;

/// <summary>
/// Passthrough 模式 Session 映射（Metadata: acp:passthrough:{seeingSessionId}）。
/// </summary>
public sealed class AcpSessionStore
{
    private readonly ISessionManager _sessionManager;
    private readonly ILogger<AcpSessionStore> _logger;

    public AcpSessionStore(ISessionManager sessionManager, ILogger<AcpSessionStore> logger)
    {
        _sessionManager = sessionManager;
        _logger = logger;
    }

    public AcpSessionMapping? GetMapping(string seeingSessionId)
    {
        var session = GetSession(seeingSessionId);
        if (session == null)
            return null;

        session.Metadata.TryGetValue(AcpMetadataKeys.Passthrough(seeingSessionId), out var raw);
        return AcpSessionMapping.TryParse(raw);
    }

    public void SaveMapping(string seeingSessionId, AcpSessionMapping mapping)
    {
        var session = RequireSession(seeingSessionId);
        session.Metadata[AcpMetadataKeys.Passthrough(seeingSessionId)] = mapping.Serialize();
        Persist(session);
    }

    public void CopyForFork(string parentSessionId, string childSessionId)
    {
        var parent = GetSession(parentSessionId);
        if (parent == null)
            return;

        if (!parent.Metadata.TryGetValue(AcpMetadataKeys.Passthrough(parentSessionId), out var raw) ||
            string.IsNullOrWhiteSpace(raw))
            return;

        var child = RequireSession(childSessionId);
        child.Metadata[AcpMetadataKeys.Passthrough(childSessionId)] = raw;
        Persist(child);
        _logger.LogDebug("Copied passthrough mapping from {Parent} to {Child}", parentSessionId, childSessionId);
    }

    public void ClearOnDestroy(string seeingSessionId)
    {
        var session = GetSession(seeingSessionId);
        if (session == null)
            return;

        session.Metadata.Remove(AcpMetadataKeys.Passthrough(seeingSessionId));
        Persist(session);
    }

    private SessionData? GetSession(string sessionId)
    {
        try
        {
            return _sessionManager.Get(sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get session {SessionId}", sessionId);
            return null;
        }
    }

    private SessionData RequireSession(string sessionId)
    {
        var session = GetSession(sessionId);
        if (session == null)
            throw new InvalidOperationException($"Session '{sessionId}' not found.");

        return session;
    }

    private void Persist(SessionData session)
    {
        try
        {
            _sessionManager.SaveAsync(session.Id).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist session {SessionId}", session.Id);
        }
    }
}
