using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Reminders;
using Seeing.Agent.Gateway.Core;
using Seeing.Agent.Scheduler.Abstractions;
using Seeing.Agent.Scheduler.Models;
using Seeing.Gateway.Models;
using Seeing.Gateway.Protocol;
using Seeing.Session.Core;

namespace Seeing.Agent.Gateway.Scheduling;

/// <summary>Gateway 通道投递 — 写入 Session、推送 WebSocket 事件、按 channel 出站</summary>
public sealed class GatewayScheduleDispatcher : IScheduledJobDispatcher
{
    private readonly ISessionManager _sessionManager;
    private readonly GatewayConnectionManager? _connectionManager;
    private readonly Func<GatewayChannelOutboundPayload, bool>? _pushChannelOutbound;
    private readonly ILogger<GatewayScheduleDispatcher> _logger;

    public GatewayScheduleDispatcher(
        ISessionManager sessionManager,
        ILogger<GatewayScheduleDispatcher> logger,
        GatewayConnectionManager? connectionManager = null,
        Func<GatewayChannelOutboundPayload, bool>? pushChannelOutbound = null)
    {
        _sessionManager = sessionManager;
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
            var session = _sessionManager.Get(request.SessionId)
                ?? await _sessionManager.EnsureSessionAsync(request.SessionId).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(request.UserInput))
            {
                var userMessage = SystemReminderRenderer.ToUserMessage(request.UserInput);
                await _sessionManager.AddMessageAsync(request.SessionId, userMessage, ct).ConfigureAwait(false);
            }

            var assistantMessage = SessionMessage.AssistantMessage(request.Content);
            await _sessionManager.AddMessageAsync(request.SessionId, assistantMessage, ct).ConfigureAwait(false);

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

            var channel = session.ChannelId;
            var userId = session.UserId;
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
                    SessionId = request.SessionId,
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
