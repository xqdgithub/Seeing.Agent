using Seeing.Agent.Core;
using Seeing.Agent.Llm;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Session.Core;

namespace Seeing.Agent.Gateway.Core;

/// <summary>
/// 封装 <see cref="ISessionManager.EnsureSessionAsync"/>，统一 Gateway 会话创建逻辑。
/// </summary>
public sealed class GatewaySessionResolver
{
    private readonly ISessionManager _sessionManager;
    private readonly AgentSelectionResolver _selectionResolver;
    private readonly IModelManager _modelManager;

    public GatewaySessionResolver(
        ISessionManager sessionManager,
        AgentSelectionResolver selectionResolver,
        IModelManager modelManager)
    {
        _sessionManager = sessionManager;
        _selectionResolver = selectionResolver;
        _modelManager = modelManager;
    }

    /// <summary>确保会话存在，不存在则按指定 ID 创建，并补齐 Native 默认模型</summary>
    public async Task<SessionData> EnsureSessionAsync(
        string sessionId,
        string? agentId = null,
        CancellationToken cancellationToken = default)
    {
        var selectedAgent = await _selectionResolver.ResolveAgentIdAsync(agentId, sessionSelectedAgent: null, cancellationToken)
            .ConfigureAwait(false);
        var session = await _sessionManager.EnsureSessionAsync(sessionId, selectedAgent: selectedAgent)
            .ConfigureAwait(false);

        await ApplyDefaultModelIfNeededAsync(session, selectedAgent, cancellationToken).ConfigureAwait(false);
        return session;
    }

    private async Task ApplyDefaultModelIfNeededAsync(
        SessionData session,
        string agentId,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(session.SelectedModel))
            return;

        cancellationToken.ThrowIfCancellationRequested();
        if (_modelManager.SeedSessionModel(session, agentId))
            await _sessionManager.SaveAsync(session.Id).ConfigureAwait(false);
    }
}
