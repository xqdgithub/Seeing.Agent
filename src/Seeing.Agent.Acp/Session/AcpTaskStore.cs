using Microsoft.Extensions.Logging;
using Seeing.Agent.Acp.Configuration;
using Seeing.Session.Core;

namespace Seeing.Agent.Acp.Session;

/// <summary>
/// acpTool 任务 Session 映射（Metadata: acp:task:{taskId}）。
/// </summary>
public sealed class AcpTaskStore
{
    private readonly ISessionManager _sessionManager;
    private readonly ILogger<AcpTaskStore> _logger;

    public AcpTaskStore(ISessionManager sessionManager, ILogger<AcpTaskStore> logger)
    {
        _sessionManager = sessionManager;
        _logger = logger;
    }

    public AcpSessionMapping? GetMapping(string taskId)
    {
        var session = GetSession(taskId);
        if (session == null)
            return null;

        session.Metadata.TryGetValue(AcpMetadataKeys.Task(taskId), out var raw);
        return AcpSessionMapping.TryParse(raw);
    }

    public void SaveMapping(string taskId, AcpSessionMapping mapping)
    {
        var session = RequireSession(taskId);
        session.Metadata[AcpMetadataKeys.Task(taskId)] = mapping.Serialize();
        Persist(session);
    }

    public void ClearOnDestroy(string taskId)
    {
        var session = GetSession(taskId);
        if (session == null)
            return;

        session.Metadata.Remove(AcpMetadataKeys.Task(taskId));
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
            _logger.LogDebug(ex, "Failed to get task session {TaskId}", sessionId);
            return null;
        }
    }

    private SessionData RequireSession(string sessionId)
    {
        var session = GetSession(sessionId);
        if (session == null)
            throw new InvalidOperationException($"Task session '{sessionId}' not found.");

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
            _logger.LogWarning(ex, "Failed to persist task session {SessionId}", session.Id);
        }
    }
}
