using Seeing.Agent.Core.Interfaces;
using Seeing.Gateway.Models;
using Seeing.Session.Core;
using Seeing.Session.Management;

namespace Seeing.Agent.Gateway.Core;

/// <summary>Gateway 会话管理（重置消息历史等）</summary>
public sealed class GatewaySessionService
{
    private readonly SessionManager _sessionManager;
    private readonly IAgentRegistry _agentRegistry;

    public GatewaySessionService(SessionManager sessionManager, IAgentRegistry agentRegistry)
    {
        _sessionManager = sessionManager;
        _agentRegistry = agentRegistry;
    }

    /// <summary>清空指定会话的消息历史并重置默认 Agent 选择</summary>
    public async Task<GatewaySessionResetResult?> ResetAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return null;

        var session = _sessionManager.Get(sessionId) ?? await _sessionManager.LoadAsync(sessionId).ConfigureAwait(false);
        if (session == null)
            return null;

        cancellationToken.ThrowIfCancellationRequested();

        session.ClearMessages();
        session.Context.Clear();
        session.SelectedModel = string.Empty;
        session.SelectedModelProvider = string.Empty;
        session.SelectedAcpMode = string.Empty;
        session.SelectedAgent = await _agentRegistry.GetDefaultAgentNameAsync().ConfigureAwait(false);
        session.LastActiveAt = DateTime.Now;
        session.UpdatedAt = DateTime.Now;

        await _sessionManager.SaveAsync(sessionId).ConfigureAwait(false);

        return new GatewaySessionResetResult
        {
            SessionId = sessionId,
            Cleared = true,
            MessageCount = session.Messages.Count
        };
    }
}
