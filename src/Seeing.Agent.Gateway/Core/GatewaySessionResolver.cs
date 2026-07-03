using Seeing.Agent.Core;
using Seeing.Session.Core;
using Seeing.Session.Management;

namespace Seeing.Agent.Gateway.Core;

/// <summary>
/// 封装 <see cref="SessionManager.EnsureSessionAsync"/>，统一 Gateway 会话创建逻辑。
/// </summary>
public sealed class GatewaySessionResolver
{
    private readonly SessionManager _sessionManager;
    private readonly AgentSelectionResolver _selectionResolver;

    public GatewaySessionResolver(SessionManager sessionManager, AgentSelectionResolver selectionResolver)
    {
        _sessionManager = sessionManager;
        _selectionResolver = selectionResolver;
    }

    /// <summary>确保会话存在，不存在则按指定 ID 创建</summary>
    public async Task<SessionData> EnsureSessionAsync(
        string sessionId,
        string? agentId = null,
        CancellationToken cancellationToken = default)
    {
        var selectedAgent = await _selectionResolver.ResolveAgentIdAsync(agentId, sessionSelectedAgent: null, cancellationToken)
            .ConfigureAwait(false);
        return await _sessionManager.EnsureSessionAsync(sessionId, selectedAgent: selectedAgent).ConfigureAwait(false);
    }
}
