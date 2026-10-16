using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Channels;
using Seeing.Agent.Abstractions.Reminders;
using Seeing.Agent.Abstractions.Runtime;
using Seeing.Agent.Abstractions.Scheduling;
using Seeing.Agent.Comms.Delivery;
using Seeing.Session.Core;

namespace Seeing.Agent.Comms.Scheduling;

/// <summary>
/// 调度结果写回会话并按 <see cref="SessionData.ChannelRoute"/> 出站。
/// 不依赖 Gateway 协议类型或连接管理器。
/// </summary>
public sealed class ChannelDeliveryScheduleDispatcher : IScheduledJobDispatcher
{
    private readonly ISessionRuntime _runtime;
    private readonly ISessionManager _sessionManager;
    private readonly ISystemReminderRenderer _reminders;
    private readonly OutboundDeliveryService _delivery;
    private readonly ILogger<ChannelDeliveryScheduleDispatcher> _logger;

    public ChannelDeliveryScheduleDispatcher(
        ISessionRuntime runtime,
        ISessionManager sessionManager,
        ISystemReminderRenderer reminders,
        OutboundDeliveryService delivery,
        ILogger<ChannelDeliveryScheduleDispatcher>? logger = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _reminders = reminders ?? throw new ArgumentNullException(nameof(reminders));
        _delivery = delivery ?? throw new ArgumentNullException(nameof(delivery));
        _logger = logger ?? NullLogger<ChannelDeliveryScheduleDispatcher>.Instance;
    }

    public async Task<DispatchResult> DispatchAsync(DispatchRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(request.SessionId))
            return DispatchResult.Ok();

        var suppressPush = request.Metadata != null &&
                           request.Metadata.TryGetValue("suppress_console_push", out var suppress) &&
                           suppress is true;

        try
        {
            var session = await _sessionManager.EnsureSessionAsync(request.SessionId).ConfigureAwait(false);

            // 会话写入由 SessionScheduleDispatcher 负责，避免组合分发时双写。
            _ = _runtime;
            _ = _reminders;

            if (suppressPush)
            {
                _logger.LogDebug(
                    "Skipping channel outbound (suppress_console_push): session={SessionId}",
                    request.SessionId);
                return DispatchResult.Ok();
            }

            var route = session.ChannelRoute;
            if (route is null || string.IsNullOrWhiteSpace(route.ChannelId))
            {
                _logger.LogDebug(
                    "Skipping channel outbound (empty Session.ChannelRoute): session={SessionId}",
                    request.SessionId);
                return DispatchResult.Ok();
            }

            var conversationKind = ParseKind(route.Kind);
            var message = new OutboundMessage
            {
                Target = new DeliveryTarget
                {
                    Conversation = new ConversationInfo
                    {
                        ChannelId = route.ChannelId.Trim(),
                        Kind = conversationKind,
                        ExternalId = route.ExternalId,
                        Title = route.Title
                    },
                    RecipientUserId = conversationKind == ConversationKind.Direct ? route.ExternalId : null
                },
                DeliveryKind = OutboundDeliveryKind.SystemNotice,
                Text = request.Content,
                Source = $"scheduler.{request.Source}",
                CorrelationSessionId = request.SessionId,
                JobId = ReadJobId(request)
            };

            var delivered = await _delivery.DeliverAsync(message, ct).ConfigureAwait(false);
            if (!delivered.Delivered)
            {
                _logger.LogWarning(
                    "Channel outbound not delivered: channel={Channel} session={SessionId} kind={FailureKind} detail={Detail}",
                    route.ChannelId,
                    request.SessionId,
                    delivered.FailureKind,
                    delivered.Detail);
            }

            return DispatchResult.Ok();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Schedule dispatch failed for session {SessionId}", request.SessionId);
            return DispatchResult.Fail(ex.Message);
        }
    }

    private static string? ReadJobId(DispatchRequest request)
    {
        if (request.Metadata is null)
            return null;

        if (request.Metadata.TryGetValue("jobId", out var jobId) && jobId is not null)
            return jobId.ToString();

        if (request.Metadata.TryGetValue("JobId", out var alt) && alt is not null)
            return alt.ToString();

        return null;
    }

    private static ConversationKind ParseKind(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
            return ConversationKind.Direct;

        return kind.Trim().ToLowerInvariant() switch
        {
            "group" => ConversationKind.Group,
            "guild" => ConversationKind.Guild,
            "direct" => ConversationKind.Direct,
            _ => Enum.TryParse<ConversationKind>(kind, ignoreCase: true, out var parsed)
                ? parsed
                : ConversationKind.Direct
        };
    }
}
