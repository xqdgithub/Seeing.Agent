using Microsoft.Extensions.Logging;
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
    private readonly ILogger<GatewayScheduleDispatcher> _logger;

    public GatewayScheduleDispatcher(
        ISessionManager sessionManager,
        ILogger<GatewayScheduleDispatcher> logger,
        GatewayConnectionManager? connectionManager = null)
    {
        _sessionManager = sessionManager;
        _connectionManager = connectionManager;
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
            await _sessionManager.EnsureSessionAsync(request.SessionId)
                .ConfigureAwait(false);

            if (!string.IsNullOrEmpty(request.UserInput))
            {
                var userMessage = SessionMessage.UserMessage(request.UserInput);
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

            if (!string.IsNullOrWhiteSpace(request.Channel) && _connectionManager != null)
            {
                var delivered = _connectionManager.PushChannelOutbound(new GatewayChannelOutboundPayload
                {
                    Channel = request.Channel.Trim(),
                    SessionId = request.SessionId,
                    Text = request.Content,
                    Source = $"scheduler.{request.Source}",
                    UserId = request.UserId
                });

                if (!delivered)
                {
                    _logger.LogWarning(
                        "Channel outbound not delivered (no registered host): channel={Channel} session={SessionId}",
                        request.Channel,
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
