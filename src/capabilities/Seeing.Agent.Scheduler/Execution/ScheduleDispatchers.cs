using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Reminders;
using Seeing.Agent.Abstractions.Runtime;
using Seeing.Agent.Abstractions.Scheduling;
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

/// <summary>写入 Session 的投递实现（统一走 <see cref="ISessionRuntime.DeliverAsync"/>，保持"写回不唤醒"）</summary>
public sealed class SessionScheduleDispatcher : IScheduledJobDispatcher
{
    private readonly ISessionRuntime _runtime;
    private readonly ISessionManager _sessionManager;
    private readonly ISystemReminderRenderer _reminders;
    private readonly ILogger<SessionScheduleDispatcher> _logger;

    public SessionScheduleDispatcher(
        ISessionRuntime runtime,
        ISessionManager sessionManager,
        ISystemReminderRenderer reminders,
        ILogger<SessionScheduleDispatcher> logger)
    {
        _runtime = runtime;
        _sessionManager = sessionManager;
        _reminders = reminders;
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
                // 既有调用方（AgentJob/HeartbeatJob）已包裹为 system-reminder 信封；此处解包后
                // 由运行时以 Presentation=System 统一重渲染（避免双重包裹），并沿用原信封的
                // source/kind（cron / heartbeat），保持既有语义。
                var source = SystemReminder.Sources.Job;
                var kind = SystemReminder.Kinds.Cron;
                var body = request.UserInput;
                if (_reminders.TryParse(request.UserInput!, out var parts))
                {
                    body = parts.Task;
                    if (!string.IsNullOrEmpty(parts.Source))
                        source = parts.Source;
                    if (!string.IsNullOrEmpty(parts.Kind))
                        kind = parts.Kind;
                }

                await _runtime.DeliverAsync(new InboxMessage
                {
                    TargetSessionId = request.SessionId!,
                    Sender = new InboxSender { Kind = InboxSenderKind.System, Id = "cron" },
                    Body = body,
                    Delivery = InboxDelivery.Inject,
                    Presentation = InboxPresentation.System,
                    SourceTag = source,
                    Kind = kind
                }, ct).ConfigureAwait(false);
            }

            // 再保存助手响应（外部产出写回：落盘即 consumed，不唤醒）
            await _runtime.DeliverAsync(new InboxMessage
            {
                TargetSessionId = request.SessionId!,
                Sender = new InboxSender { Kind = InboxSenderKind.System, Id = "cron" },
                Body = request.Content,
                Delivery = InboxDelivery.Inject,
                Presentation = InboxPresentation.Assistant,
                SourceTag = SystemReminder.Sources.Job,
                Kind = SystemReminder.Kinds.Cron
            }, ct).ConfigureAwait(false);

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
