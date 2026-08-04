using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Seeing.Session.Core;

namespace Seeing.Agent.Core.Scheduling;

/// <summary>
/// 默认 Loop 调度实现。
/// </summary>
public sealed class AgentLoopScheduler : IAgentLoopScheduler
{
    private readonly ConcurrentDictionary<string, byte> _busy = new(StringComparer.Ordinal);
    private readonly ISessionManager? _sessionManager;
    private readonly ILogger<AgentLoopScheduler> _logger;
    private Func<string, CancellationToken, Task>? _resumeHandler;

    public AgentLoopScheduler(
        ILogger<AgentLoopScheduler> logger,
        ISessionManager? sessionManager = null)
    {
        _logger = logger;
        _sessionManager = sessionManager;
    }

    public void SetLoopBusy(string sessionId, bool busy)
    {
        if (busy)
        {
            _busy[sessionId] = 1;
            return;
        }

        _busy.TryRemove(sessionId, out _);

        // 父 Loop 刚空闲：补唤醒遗留 synthetic（后台 Task 完成时父仍 busy 会跳过 resume）
        _ = Task.Run(async () =>
        {
            try
            {
                await TryResumeWhenIdleAsync(sessionId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SetLoopBusy(false) 后自动 resume 失败: {SessionId}", sessionId);
            }
        });
    }

    public bool IsLoopBusy(string sessionId) => _busy.ContainsKey(sessionId);

    public void RegisterResumeHandler(Func<string, CancellationToken, Task> handler) =>
        _resumeHandler = handler;

    public async Task InjectSyntheticAsync(
        string sessionId,
        string text,
        IDictionary<string, string>? metadata = null,
        CancellationToken ct = default)
    {
        if (_sessionManager == null)
        {
            _logger.LogWarning("InjectSyntheticAsync: ISessionManager 未配置，跳过注入 {SessionId}", sessionId);
            return;
        }

        var meta = new Dictionary<string, object>();
        if (metadata != null)
        {
            foreach (var kv in metadata)
                meta[kv.Key] = kv.Value;
        }
        meta["synthetic"] = "true";

        var message = new SessionMessage
        {
            Id = Guid.NewGuid().ToString("N"),
            Role = "user",
            Content = text,
            CreatedAt = DateTime.UtcNow,
            Metadata = meta
        };

        await _sessionManager.AddMessageAsync(sessionId, message, ct);
        _logger.LogInformation("已注入 synthetic 消息到会话 {SessionId}", sessionId);
    }

    public async Task<bool> TryResumeWhenIdleAsync(string sessionId, CancellationToken ct = default)
    {
        if (IsLoopBusy(sessionId))
        {
            _logger.LogDebug("TryResumeWhenIdle: {SessionId} 仍忙碌，跳过", sessionId);
            return false;
        }

        if (_resumeHandler == null)
        {
            _logger.LogWarning("TryResumeWhenIdle: 未注册 ResumeHandler，跳过 {SessionId}", sessionId);
            return false;
        }

        // 无待处理 synthetic 用户消息时禁止 resume，避免空输入再次跑完整 Agent Loop
        if (_sessionManager != null)
        {
            var session = _sessionManager.Get(sessionId);
            if (session != null && !HasPendingSyntheticUserMessage(session))
            {
                _logger.LogDebug(
                    "TryResumeWhenIdle: {SessionId} 无待处理 synthetic 消息，跳过",
                    sessionId);
                return false;
            }
        }

        // busy 由 ResumeHandler / ExecutionJobService 管理，此处不抢占
        await _resumeHandler(sessionId, ct);
        return true;
    }

    /// <summary>
    /// 是否存在尚未被助手回复消费的 synthetic 用户消息（位于最后一条 assistant 之后）。
    /// </summary>
    internal static bool HasPendingSyntheticUserMessage(SessionData session)
    {
        var lastAssistant = -1;
        for (var i = session.Messages.Count - 1; i >= 0; i--)
        {
            if (string.Equals(session.Messages[i].Role, "assistant", StringComparison.OrdinalIgnoreCase))
            {
                lastAssistant = i;
                break;
            }
        }

        for (var i = lastAssistant + 1; i < session.Messages.Count; i++)
        {
            var msg = session.Messages[i];
            if (!string.Equals(msg.Role, "user", StringComparison.OrdinalIgnoreCase))
                continue;

            if (msg.Metadata != null && msg.Metadata.ContainsKey("synthetic"))
                return true;
        }

        return false;
    }
}
