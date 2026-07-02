using Seeing.Agent.Configuration;
using Seeing.Session.Core;
using Seeing.Session.Management;

namespace Seeing.Agent.Gateway.Core;

/// <summary>
/// 封装 <see cref="SessionManager.EnsureSessionAsync"/>，统一 Gateway 会话创建逻辑。
/// </summary>
public sealed class GatewaySessionResolver
{
    private readonly SessionManager _sessionManager;
    private readonly GatewayOptions _options;

    public GatewaySessionResolver(SessionManager sessionManager, GatewayOptions options)
    {
        _sessionManager = sessionManager;
        _options = options;
    }

    /// <summary>确保会话存在，不存在则按指定 ID 创建</summary>
    public Task<SessionData> EnsureSessionAsync(string sessionId, string? agentId = null)
    {
        var selectedAgent = agentId ?? _options.DefaultAgentId;
        return _sessionManager.EnsureSessionAsync(sessionId, selectedAgent: selectedAgent);
    }
}
