using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Reminders;
using Seeing.Agent.Scheduler.Abstractions;
using Seeing.Agent.Scheduler.Models;
using Seeing.Session.Core;

namespace Seeing.Agent.Scheduler.Execution;

/// <summary>日志投递（fallback）</summary>
public sealed class LogScheduleDispatcher : IScheduledJobDispatcher
{
    private readonly ILogger<LogScheduleDispatcher> _logger;

    public LogScheduleDispatcher(ILogger<LogScheduleDispatcher> logger)
    {
        _logger = logger;
    }

    public Task<DispatchResult> DispatchAsync(DispatchRequest request, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "Schedule dispatch [{Source}/{TaskType}] session={SessionId} len={Length}",
            request.Source,
            request.TaskType,
            request.SessionId,
            request.Content.Length);

        return Task.FromResult(DispatchResult.Ok());
    }
}

/// <summary>写入 Session 的投递实现</summary>
public sealed class SessionScheduleDispatcher : IScheduledJobDispatcher
{
    private readonly ISessionManager _sessionManager;
    private readonly ILogger<SessionScheduleDispatcher> _logger;

    public SessionScheduleDispatcher(ISessionManager sessionManager, ILogger<SessionScheduleDispatcher> logger)
    {
        _sessionManager = sessionManager;
        _logger = logger;
    }

    public async Task<DispatchResult> DispatchAsync(DispatchRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(request.SessionId))
            return DispatchResult.Ok();

        try
        {
            await _sessionManager.EnsureSessionAsync(request.SessionId)
                .ConfigureAwait(false);

            if (!string.IsNullOrEmpty(request.UserInput))
            {
                var userMessage = SystemReminderRenderer.ToUserMessage(request.UserInput);
                await _sessionManager.AddMessageAsync(request.SessionId, userMessage, ct).ConfigureAwait(false);
            }

            // 再保存助手响应
            var assistantMessage = SessionMessage.AssistantMessage(request.Content);
            await _sessionManager.AddMessageAsync(request.SessionId, assistantMessage, ct).ConfigureAwait(false);

            return DispatchResult.Ok();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to dispatch to session {SessionId}", request.SessionId);
            return DispatchResult.Fail(ex.Message);
        }
    }
}

/// <summary>组合多个投递器</summary>
public sealed class CompositeScheduleDispatcher : IScheduledJobDispatcher
{
    private readonly IReadOnlyList<IScheduledJobDispatcher> _dispatchers;

    public CompositeScheduleDispatcher(IEnumerable<IScheduledJobDispatcher> dispatchers)
    {
        _dispatchers = dispatchers.ToList();
    }

    public async Task<DispatchResult> DispatchAsync(DispatchRequest request, CancellationToken ct = default)
    {
        DispatchResult? last = null;
        foreach (var dispatcher in _dispatchers)
        {
            last = await dispatcher.DispatchAsync(request, ct).ConfigureAwait(false);
            if (!last.Success)
                return last;
        }

        return last ?? DispatchResult.Ok();
    }
}
