using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Reminders;
using Seeing.Agent.Abstractions.Runtime;
using Seeing.Agent.Abstractions.Scheduling;
using Seeing.Agent.Gateway.Core;
using Seeing.Gateway.Models;
using Seeing.Gateway.Protocol;
using Seeing.Session.Core;

namespace Seeing.Agent.Gateway.Scheduling;

/// <summary>Gateway 通道投递 — 经会话运行时写回（不唤醒），推送 WebSocket 事件、按 channel 出站</summary>
public sealed class GatewayScheduleDispatcher : IScheduledJobDispatcher
{
    private readonly ISessionRuntime _runtime;
    private readonly ISessionManager _sessionManager;
    private readonly ISystemReminderRenderer _reminders;
    private readonly GatewayConnectionManager? _connectionManager;
    private readonly Func<GatewayChannelOutboundPayload, bool>? _pushChannelOutbound;
    private readonly ILogger<GatewayScheduleDispatcher> _logger;

    public GatewayScheduleDispatcher(
        ISessionRuntime runtime,
        ISessionManager sessionManager,
        ISystemReminderRenderer reminders,
        ILogger<GatewayScheduleDispatcher> logger,
        GatewayConnectionManager? connectionManager = null,
        Func<GatewayChannelOutboundPayload, bool>? pushChannelOutbound = null)
    {
        _runtime = runtime;
        _sessionManager = sessionManager;
        _reminders = reminders;
        _connectionManager = connectionManager;
        _pushChannelOutbound = pushChannelOutbound
            ?? (connectionManager is null ? null : connectionManager.PushChannelOutbound);
        _logger = logger;
    }

    public async Task<DispatchResult> DispatchAsync(DispatchRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(request.SessionId))
            return DispatchResult.Ok();

        var suppressPush = request.Metadata != null &&
                           request.Metadata.TryGetValue("suppress_console_push", out var v) &&
                           v is true;

        try
        {
            var session = await _sessionManager.EnsureSessionAsync(request.SessionId).ConfigureAwait(false);

            // reminder（用户输入）→ System 信封，落盘不唤醒（spec §9）
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
                    TargetSessionId = request.SessionId,
                    Sender = new InboxSender { Kind = InboxSenderKind.System, Id = "gateway" },
                    Body = body,
                    Delivery = InboxDelivery.Inject,
                    Presentation = InboxPresentation.System,
                    SourceTag = source,
                    Kind = kind
                }, ct).ConfigureAwait(false);
            }

            // 产出写回 → Assistant，落盘即 consumed、不唤醒（spec §9）
            await _runtime.DeliverAsync(new InboxMessage
            {
                TargetSessionId = request.SessionId,
                Sender = new InboxSender { Kind = InboxSenderKind.System, Id = "gateway" },
                Body = request.Content,
                Delivery = InboxDelivery.Inject,
                Presentation = InboxPresentation.Assistant,
                SourceTag = $"scheduler.{request.Source}",
                Kind = SystemReminder.Kinds.Cron
            }, ct).ConfigureAwait(false);

            if (!suppressPush && _connectionManager != null)
            {
                _connectionManager.PushEvent(request.SessionId, new GatewayEvent
                {
                    Object = GatewayEventObject.Message,
                    Status = GatewayEventStatus.Completed,
                    SessionId = request.SessionId,
                    SourceType = $"scheduler.{request.Source}",
                    Data = new GatewayEventData
                    {
                        Role = "assistant",
                        Text = request.Content
                    }
                });
            }

            var channel = session.ChannelRoute?.ChannelId;
            var userId = session.ChannelRoute?.ExternalId;
            if (string.IsNullOrWhiteSpace(channel))
            {
                _logger.LogDebug(
                    "Skipping channel outbound (empty Session.ChannelId): session={SessionId}",
                    request.SessionId);
            }
            else if (_pushChannelOutbound != null)
            {
                var delivered = _pushChannelOutbound(new GatewayChannelOutboundPayload
                {
                    Channel = channel.Trim(),
                    CorrelationSessionId = request.SessionId,
                    Text = request.Content,
                    Source = $"scheduler.{request.Source}",
                    UserId = userId
                });

                if (!delivered)
                {
                    _logger.LogWarning(
                        "Channel outbound not delivered (no registered host): channel={Channel} session={SessionId}",
                        channel,
                        request.SessionId);
                }
            }

            return DispatchResult.Ok();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gateway schedule dispatch failed for session {SessionId}", request.SessionId);
            return DispatchResult.Fail(ex.Message);
        }
    }
}
