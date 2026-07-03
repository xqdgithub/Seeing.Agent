using Microsoft.Extensions.Logging;
using Seeing.Agent.Acp.Session;
using Seeing.Agent.Acp.Transport;
using Seeing.Agent.Core.Hooks;

namespace Seeing.Agent.Acp.Hosting;

/// <summary>
/// Session 销毁 / Fork 时同步 ACP 映射与子进程租约。
/// </summary>
public sealed class AcpSessionLifecycleHook : IMultiHookHandler
{
    private static readonly HookSpec SessionForked = new(HookPolicy.FireAndForget, "session.forked");

    private readonly AcpSessionStore _sessionStore;
    private readonly AcpConnectionManager _connectionManager;
    private readonly ILogger<AcpSessionLifecycleHook> _logger;

    public AcpSessionLifecycleHook(
        AcpSessionStore sessionStore,
        AcpConnectionManager connectionManager,
        ILogger<AcpSessionLifecycleHook> logger)
    {
        _sessionStore = sessionStore;
        _connectionManager = connectionManager;
        _logger = logger;
    }

    public IReadOnlyList<HookSpec> Specs { get; } =
    [
        HookRegistry.SessionDestroyed,
        SessionForked
    ];

    public int Priority => 50;

    public async Task<HookResult> ExecuteAsync(HookPayload payload)
    {
        try
        {
            if (payload.Spec.Point == HookRegistry.SessionDestroyed.Point)
                await HandleDestroyedAsync(payload).ConfigureAwait(false);
            else if (payload.Spec.Point == SessionForked.Point)
                HandleForked(payload);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ACP session lifecycle hook failed for {HookPoint}", payload.Spec.Point);
        }

        return HookResult.Success;
    }

    private async Task HandleDestroyedAsync(HookPayload payload)
    {
        var sessionId = payload.SessionId;
        if (string.IsNullOrEmpty(sessionId))
            return;

        var mapping = _sessionStore.GetMapping(sessionId);
        if (mapping != null)
        {
            var leaseKey = AcpConnectionManager.BuildPassthroughKey(mapping.BackendId, sessionId);
            await _connectionManager.ReleaseByKeyAsync(leaseKey).ConfigureAwait(false);
        }

        _sessionStore.ClearOnDestroy(sessionId);
        _logger.LogDebug("Cleared ACP passthrough mapping for destroyed session {SessionId}", sessionId);
    }

    private void HandleForked(HookPayload payload)
    {
        var childId = payload.SessionId;
        if (string.IsNullOrEmpty(childId))
            return;

        string? parentId = null;
        if (payload.Result.TryGetValue("parentSessionId", out var parentObj))
            parentId = parentObj?.ToString();

        if (string.IsNullOrEmpty(parentId))
            return;

        _sessionStore.CopyForFork(parentId, childId);
        _logger.LogDebug("Copied ACP passthrough mapping fork {Parent} -> {Child}", parentId, childId);
    }
}
