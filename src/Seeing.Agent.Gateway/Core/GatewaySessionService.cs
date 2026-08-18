using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Core;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Llm;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Gateway.Models;
using Seeing.Session.Core;

namespace Seeing.Agent.Gateway.Core;

/// <summary>Gateway 会话管理（重置消息历史等）</summary>
public sealed class GatewaySessionService
{
    private readonly ISessionManager _sessionManager;
    private readonly IAgentRegistry _agentRegistry;
    private readonly IAgentRuntimeManager _runtimeManager;
    private readonly IModelManager _modelManager;

    public GatewaySessionService(
        ISessionManager sessionManager,
        IAgentRegistry agentRegistry,
        IAgentRuntimeManager runtimeManager,
        IModelManager modelManager)
    {
        _sessionManager = sessionManager;
        _agentRegistry = agentRegistry;
        _runtimeManager = runtimeManager;
        _modelManager = modelManager;
    }

    /// <summary>清空指定会话的消息历史并重置默认 Agent / Model 选择</summary>
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
        session.SelectedAcpMode = string.Empty;
        session.SelectedAgent = await _runtimeManager.GetDefaultAgentNameAsync().ConfigureAwait(false);
        session.LastActiveAt = DateTime.Now;
        session.UpdatedAt = DateTime.Now;

        var agent = await _agentRegistry.GetAgentAsync(session.SelectedAgent).ConfigureAwait(false);
        if (agent?.Runtime != AgentRuntime.AcpPassthrough)
            _modelManager.SeedSessionModel(session, session.SelectedAgent);

        await _sessionManager.SaveAsync(sessionId).ConfigureAwait(false);

        return new GatewaySessionResetResult
        {
            SessionId = sessionId,
            Cleared = true,
            MessageCount = session.Messages.Count
        };
    }
}
