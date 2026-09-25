using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Session.Core;

namespace Seeing.Agent.Core.Permission;

/// <summary>
/// 会话销毁清理订阅组件（P1-16）：订阅 <c>session.destroyed</c> Hook
/// （<see cref="Seeing.Session.Management.SessionManager.Delete"/> 触发，payload.result 含 session），
/// 收敛该会话全部在途权限请求（Deny + Cancellation，唤醒等待方）并清除授权记忆与白名单目录，
/// 避免常驻宿主内存随会话删除单调增长。
/// </summary>
public sealed class SessionDestroyedCleanupHook : IHookHandler
{
    private readonly IPermissionRequestManager _requests;
    private readonly IPermissionGrantStore _grants;
    private readonly ILogger<SessionDestroyedCleanupHook> _logger;

    /// <summary>创建会话销毁清理组件。</summary>
    public SessionDestroyedCleanupHook(
        IPermissionRequestManager requests,
        IPermissionGrantStore grants,
        ILogger<SessionDestroyedCleanupHook> logger)
    {
        _requests = requests ?? throw new ArgumentNullException(nameof(requests));
        _grants = grants ?? throw new ArgumentNullException(nameof(grants));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public HookSpec Spec => HookRegistry.SessionDestroyed;

    /// <inheritdoc />
    public int Priority => 100;

    /// <inheritdoc />
    public Task<HookResult> ExecuteAsync(HookPayload payload)
    {
        var sessionId = ResolveSessionId(payload);
        if (string.IsNullOrEmpty(sessionId))
        {
            _logger.LogDebug("会话销毁清理：payload 无 sessionId，跳过");
            return Task.FromResult(HookResult.Success);
        }

        // 1) 收敛在途审批：以取消语义 Deny，唤醒等待方并广播 Dismiss
        foreach (var request in _requests.GetPending(sessionId))
        {
            if (string.IsNullOrEmpty(request.RequestId))
                continue;

            try
            {
                _requests.TryResolve(
                    request.RequestId,
                    PermissionEffect.Deny,
                    PermissionGrantScope.Once,
                    PermissionResolvedBy.Cancellation,
                    "会话已销毁",
                    sessionId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "会话销毁清理：收敛在途审批失败 SessionId={SessionId}, RequestId={RequestId}",
                    sessionId, request.RequestId);
            }
        }

        // 2) 清除授权记忆 + 白名单目录
        try
        {
            _grants.ClearSession(sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "会话销毁清理：清除授权存储失败 SessionId={SessionId}", sessionId);
        }

        _logger.LogDebug("会话销毁清理完成: SessionId={SessionId}", sessionId);
        return Task.FromResult(HookResult.Success);
    }

    /// <summary>解析会话 ID：优先 payload.SessionId，其次 payload.result["session"].Id。</summary>
    private static string? ResolveSessionId(HookPayload payload)
    {
        if (!string.IsNullOrEmpty(payload.SessionId))
            return payload.SessionId;

        return payload.GetResult<SessionData>("session")?.Id;
    }
}

/// <summary>
/// 宿主启动时把 <see cref="SessionDestroyedCleanupHook"/> 注册进 Hook 管理器（停止时反注册）。
/// </summary>
internal sealed class SessionDestroyedCleanupHookRegistrar : IHostedService
{
    private readonly IHookManager _hookManager;
    private readonly SessionDestroyedCleanupHook _hook;

    public SessionDestroyedCleanupHookRegistrar(
        IHookManager hookManager,
        SessionDestroyedCleanupHook hook)
    {
        _hookManager = hookManager ?? throw new ArgumentNullException(nameof(hookManager));
        _hook = hook ?? throw new ArgumentNullException(nameof(hook));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _hookManager.Register(_hook);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _hookManager.Remove(_hook);
        return Task.CompletedTask;
    }
}
