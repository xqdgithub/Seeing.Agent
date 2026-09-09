using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Llm;
using Seeing.Session.Core;

namespace Seeing.Agent.Gateway.Core;

/// <summary>
/// 封装 <see cref="ISessionManager.EnsureSessionAsync"/>，统一 Gateway 会话创建逻辑。
/// </summary>
public sealed class GatewaySessionResolver
{
    private readonly ISessionManager _sessionManager;
    private readonly IAgentSelectionResolver _selectionResolver;
    private readonly IModelManager _modelManager;
    private readonly IDefaultWorkModeProvider? _defaultWorkMode;

    public GatewaySessionResolver(
        ISessionManager sessionManager,
        IAgentSelectionResolver selectionResolver,
        IModelManager modelManager,
        IDefaultWorkModeProvider? defaultWorkMode = null)
    {
        _sessionManager = sessionManager;
        _selectionResolver = selectionResolver;
        _modelManager = modelManager;
        _defaultWorkMode = defaultWorkMode;
    }

    /// <summary>确保会话存在，不存在则按指定 ID 创建，并补齐 Native 默认模型与进程默认 Scenario</summary>
    public async Task<SessionData> EnsureSessionAsync(
        string sessionId,
        string? agentId = null,
        CancellationToken cancellationToken = default)
    {
        var selectedAgent = await _selectionResolver.ResolveAgentIdAsync(agentId, sessionSelectedAgent: null, cancellationToken)
            .ConfigureAwait(false);
        var existedInCache = _sessionManager.Get(sessionId) is not null;
        var session = await _sessionManager.EnsureSessionAsync(
                sessionId,
                selectedAgent: selectedAgent,
                scenario: _defaultWorkMode?.GetDefaultScenario())
            .ConfigureAwait(false);

        // 新建或从存储加载进缓存后落盘，确保持久化物化的 Scenario
        if (!existedInCache)
            await _sessionManager.SaveAsync(session.Id).ConfigureAwait(false);

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
