using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Session.Core;

namespace Seeing.Agent.Hosting.Execution;

/// <summary>
/// <see cref="ISessionToolExposureWriter"/> 唯一实现：把写入集中到这一处，
/// 其他组件（ExecutionRunner / Scheduler）不得再直接操作 <c>SessionData.ToolExposure</c>。
/// </summary>
public sealed class SessionToolExposureWriter : ISessionToolExposureWriter
{
    private readonly ISessionManager _sessionManager;
    private readonly ILogger<SessionToolExposureWriter>? _logger;

    public SessionToolExposureWriter(
        ISessionManager sessionManager,
        ILogger<SessionToolExposureWriter>? logger = null)
    {
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _logger = logger;
    }

    /// <inheritdoc />
    public void PromotePending(SessionData session)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.ToolExposure.PromotePending();
    }

    /// <inheritdoc />
    public async Task<bool> PromotePendingAsync(
        string sessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return false;

        try
        {
            var session = _sessionManager.Get(sessionId)
                ?? await _sessionManager.LoadAsync(sessionId).ConfigureAwait(false);
            if (session is null)
            {
                _logger?.LogDebug("提升待生效发现：会话不可用，跳过: {SessionId}", sessionId);
                return false;
            }

            session.ToolExposure.PromotePending();
            _logger?.LogDebug("提升待生效发现完成: {SessionId}", sessionId);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 旁路语义：提升失败不得阻断任务，降级为"本轮不提升"。
            _logger?.LogWarning(ex, "提升待生效发现失败，跳过: {SessionId}", sessionId);
            return false;
        }
    }

    /// <inheritdoc />
    public void Commit(
        SessionData session,
        IReadOnlyList<string>? discovered,
        bool escalated,
        int schemaTokens,
        int catalogTokens)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.ToolExposure.Commit(discovered, escalated, schemaTokens, catalogTokens);
    }
}
