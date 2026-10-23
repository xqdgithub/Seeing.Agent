using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Channels;
using Seeing.Gateway.Channels;
using Seeing.Gateway.Client;
using Seeing.Gateway.Models;
using Seeing.Gateway.Protocol;
using Seeing.Gateway.QQ.Connection;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Seeing.Gateway.QQ;

/// <summary>
/// QQ Channel Bridge：QQ Bot WS/HTTP ↔ Gateway WebSocket。
/// 会话面与通道宿主面共用同一 <see cref="WebSocketGatewayClient"/> 实例。
/// </summary>
public sealed class QQChannelBridge : IChannelBridge, IAsyncDisposable
{
    private readonly QQOptions _options;
    private readonly GatewayClientCommonOptions _commonOptions;
    private readonly QQWebSocketClient _qqWs;
    private readonly QQHttpApiClient _qqHttp;
    private readonly ISessionClient _sessionClient;
    private readonly IGatewayChannelHostClient _hostClient;
    private readonly QQChannelAdapter _adapter;
    private readonly QQMediaFetcher _mediaFetcher;
    private readonly QQPermissionPolicy _permissionPolicy;
    private readonly QQPermissionState _permissionState;
    private readonly QQSessionTracker _sessionTracker;
    private readonly QQCommandInterceptor _commandInterceptor;
    private readonly QQPermissionResponder _permissionResponder;
    private readonly QQChannelHealth _health;
    private readonly ILogger<QQChannelBridge> _logger;
    private readonly ConcurrentDictionary<string, byte> _processedMessageIds = new();
    private CancellationTokenSource? _cts;

    public QQChannelBridge(
        IOptions<QQOptions> options,
        IOptions<GatewayClientCommonOptions> commonOptions,
        QQWebSocketClient qqWs,
        QQHttpApiClient qqHttp,
        ISessionClient sessionClient,
        IGatewayChannelHostClient hostClient,
        QQChannelAdapter adapter,
        QQMediaFetcher mediaFetcher,
        QQPermissionPolicy permissionPolicy,
        QQPermissionState permissionState,
        QQSessionTracker sessionTracker,
        QQCommandInterceptor commandInterceptor,
        QQPermissionResponder permissionResponder,
        QQChannelHealth health,
        ILogger<QQChannelBridge> logger)
    {
        _options = options.Value;
        _commonOptions = commonOptions.Value;
        _qqWs = qqWs;
        _qqHttp = qqHttp;
        _sessionClient = sessionClient;
        _hostClient = hostClient;
        _adapter = adapter;
        _mediaFetcher = mediaFetcher;
        _permissionPolicy = permissionPolicy;
        _permissionState = permissionState;
        _sessionTracker = sessionTracker;
        _commandInterceptor = commandInterceptor;
        _permissionResponder = permissionResponder;
        _health = health;
        _logger = logger;
    }

    public string ChannelId => QQChannelAdapter.Channel;

    /// <inheritdoc />
    public ChannelCapabilities Capabilities => _adapter.Capabilities;

    IGatewayConnection IChannelBridge.GatewayConnection =>
        _hostClient as IGatewayConnection
        ?? throw new InvalidOperationException("QQ 通道宿主客户端必须同时实现 IGatewayConnection");

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("QQ Channel 未启用");
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.AppId) || string.IsNullOrWhiteSpace(_options.ClientSecret))
            throw new InvalidOperationException("QQ AppId 和 ClientSecret 必须配置");

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _qqWs.OnDispatch += HandleDispatchAsync;
        _hostClient.OnChannelOutbound += HandleChannelOutboundAsync;
        await _qqWs.StartAsync(_cts.Token).ConfigureAwait(false);
        var snap = _health.GetSnapshot();
        _logger.LogInformation(
            "QQ Channel Bridge 已启动 status={Status} detail={Detail}",
            snap.Status,
            snap.Detail);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _qqWs.OnDispatch -= HandleDispatchAsync;
        _hostClient.OnChannelOutbound -= HandleChannelOutboundAsync;
        _cts?.Cancel();
        await _qqWs.StopAsync().ConfigureAwait(false);
        _cts?.Dispose();
        _cts = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task HandleChannelOutboundAsync(
        GatewayChannelOutboundPayload payload,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(payload.Channel, ChannelId, StringComparison.OrdinalIgnoreCase))
            return;

        if (string.IsNullOrWhiteSpace(payload.Content.Text) && payload.Content.Attachments.Count == 0)
        {
            _logger.LogWarning(
                "QQ channel outbound ignored: empty content session={SessionId}",
                payload.CorrelationSessionId);
            return;
        }

        if (payload.Conversation == null)
        {
            _logger.LogError(
                "QQ channel outbound missing conversation session={SessionId}",
                payload.CorrelationSessionId);
            return;
        }

        var message = new OutboundMessage
        {
            Target = new DeliveryTarget
            {
                Conversation = payload.Conversation,
                RecipientUserId = payload.RecipientUserId,
                ReplyToMsgId = payload.ReplyToMsgId
            },
            DeliveryKind = payload.DeliveryKind ?? OutboundDeliveryKind.Push,
            Content = payload.Content,
            Source = payload.Source,
            CorrelationSessionId = payload.CorrelationSessionId,
            JobId = payload.JobId
        };

        try
        {
            _logger.LogInformation(
                "QQ channel outbound → {Kind} session={SessionId} len={Length} attachments={Attachments} source={Source}",
                message.Target.Conversation.Kind,
                payload.CorrelationSessionId,
                payload.Content.Text?.Length ?? 0,
                payload.Content.Attachments.Count,
                payload.Source);
            var result = await _adapter.SendAsync(message, cancellationToken).ConfigureAwait(false);
            if (!result.Delivered)
            {
                _logger.LogError(
                    "QQ channel outbound failed session={SessionId} kind={Kind} detail={Detail}",
                    payload.CorrelationSessionId,
                    result.FailureKind,
                    result.Detail);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "QQ channel outbound send failed session={SessionId}", payload.CorrelationSessionId);
        }
    }

    private async Task HandleDispatchAsync(string eventType, JsonElement d, CancellationToken cancellationToken)
    {
        if (string.Equals(eventType, "INTERACTION_CREATE", StringComparison.OrdinalIgnoreCase))
        {
            var interactionId = d.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            var handled = await _permissionResponder.TryHandleInteractionAsync(d, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(interactionId))
                await _qqHttp.AckInteractionAsync(interactionId, handled ? 0 : 3, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!QQMessageParser.TryParse(eventType, d, out var parsed) || parsed == null)
        {
            if (eventType.Contains("MESSAGE", StringComparison.OrdinalIgnoreCase)
                || eventType.Contains("GROUP", StringComparison.OrdinalIgnoreCase)
                || eventType.Contains("AT_", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "QQ message event not parsed: t={EventType} payload={Payload}",
                    eventType,
                    Truncate(d.GetRawText(), 500));
            }
            return;
        }

        if (string.Equals(parsed.MessageType, "group", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrEmpty(parsed.GroupOpenId))
        {
            _logger.LogError(
                "QQ group event missing group_openid; cannot reply in group. payload={Payload}",
                Truncate(d.GetRawText(), 500));
        }

        if (parsed.IsBotAuthor)
        {
            _logger.LogInformation("QQ skip bot author message {MsgId}", parsed.MsgId);
            return;
        }

        if (!string.IsNullOrEmpty(_options.BotPrefix)
            && parsed.Text.StartsWith(_options.BotPrefix, StringComparison.Ordinal))
        {
            _logger.LogInformation("QQ skip BotPrefix echo {MsgId} text={Text}", parsed.MsgId, parsed.Text);
            return;
        }

        var ack = _options.EffectiveAckMessage;
        if (ack != null && string.Equals(parsed.Text.Trim(), ack, StringComparison.Ordinal))
        {
            _logger.LogInformation("QQ skip ack echo {MsgId}", parsed.MsgId);
            return;
        }

        if (!_processedMessageIds.TryAdd(parsed.MsgId, 0))
        {
            _logger.LogDebug("QQ duplicate msg skipped {MsgId}", parsed.MsgId);
            return;
        }

        if (!_adapter.TryMapInbound(eventType, d, _options, out var inbound) || inbound == null)
            return;

        _logger.LogInformation(
            "QQ recv {Type} from={Sender} group={Group} text={Text}",
            parsed.MessageType,
            parsed.SenderOpenId,
            parsed.GroupOpenId,
            parsed.Text.Length > 80 ? parsed.Text[..80] : parsed.Text);

        _ = ProcessMessageAsync(inbound, parsed, cancellationToken);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    private async Task ProcessMessageAsync(
        ChannelInboundMessage inbound,
        ParsedQQMessage parsed,
        CancellationToken cancellationToken)
    {
        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts?.Token ?? CancellationToken.None);
        jobCts.CancelAfter(TimeSpan.FromSeconds(_options.EffectiveProcessingMaxDurationSeconds));
        var jobToken = jobCts.Token;

        try
        {
            _adapter.RegisterInbound(parsed.MsgId, DateTimeOffset.UtcNow);
            _adapter.RegisterDirectoryInbound(inbound);

            var sessionId = _sessionTracker.Resolve(parsed);
            var cmd = _commandInterceptor.TryParse(parsed.Text, out _);
            if (cmd == QQCommandInterceptor.CommandKind.New)
            {
                sessionId = _sessionTracker.Rotate(parsed);
                await SendVisibleAsync(parsed, "已开启新对话。", jobToken).ConfigureAwait(false);
                return;
            }

            if (cmd == QQCommandInterceptor.CommandKind.Clear)
            {
                await _hostClient.ResetSessionAsync(sessionId, jobToken).ConfigureAwait(false);
                await SendVisibleAsync(parsed, "已清空当前上下文。", jobToken).ConfigureAwait(false);
                return;
            }

            if (cmd is QQCommandInterceptor.CommandKind.Subscribe or QQCommandInterceptor.CommandKind.Unsubscribe)
            {
                await HandleSubscriptionCommandAsync(parsed, cmd, jobToken).ConfigureAwait(false);
                return;
            }

            var ackText = _options.EffectiveAckMessage;
            if (ackText != null)
            {
                var ackTarget = parsed;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var prepared = _adapter.ApplyPassiveWindow(ackTarget);
                        await _qqHttp.SendStatusAsync(prepared, ackText, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "QQ ack message failed");
                    }
                });
            }

            var mediaParts = await _mediaFetcher.FetchAsync(parsed.Attachments, jobToken).ConfigureAwait(false);
            var request = QQMessageParser.ToGatewayRequest(
                parsed,
                sessionId,
                _commonOptions.Agent,
                _commonOptions.Mode,
                _commonOptions.Model,
                mediaParts);

            var reply = new GatewayAssistantReplyCollector();

            var submit = await _sessionClient.SubmitAsync(request, jobToken).ConfigureAwait(false);
            if (!submit.Success)
            {
                await SendVisibleAsync(parsed, submit.Error ?? "提交失败", CancellationToken.None).ConfigureAwait(false);
                return;
            }

            // 按 submit 关联 ID 订阅请求/应答流：跳过他轮滞留帧（含桥提前 return 残留的陈旧 execution.complete）
            await foreach (var gatewayEvent in _sessionClient.SubscribeAsync(sessionId, submit.RequestId, jobToken)
                               .ConfigureAwait(false))
            {
                var disposition = reply.Apply(gatewayEvent);
                switch (disposition)
                {
                    case GatewayReplyDisposition.Permission:
                        await HandlePermissionAsync(gatewayEvent, sessionId, parsed, jobToken).ConfigureAwait(false);
                        break;
                    case GatewayReplyDisposition.RunCompleted:
                        await SendVisibleAsync(parsed, reply.Text, jobToken).ConfigureAwait(false);
                        _sessionTracker.Touch(parsed);
                        return;
                    case GatewayReplyDisposition.RunCancelled:
                        await SendVisibleAsync(parsed, $"⚠️ {reply.TerminalMessage}", CancellationToken.None).ConfigureAwait(false);
                        return;
                    case GatewayReplyDisposition.RunFailed:
                        await SendVisibleAsync(parsed, reply.TerminalMessage ?? "执行失败", CancellationToken.None).ConfigureAwait(false);
                        return;
                }
            }

            if (!reply.IsTerminal)
                await SendVisibleAsync(parsed, reply.Text, jobToken).ConfigureAwait(false);

            _sessionTracker.Touch(parsed);
        }
        catch (OperationCanceledException) when (_cts?.IsCancellationRequested == true)
        {
            _logger.LogDebug("QQ message cancelled (bridge stopping)");
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("QQ message timed out");
            try
            {
                await SendVisibleAsync(parsed, "处理超时，请稍后重试", CancellationToken.None).ConfigureAwait(false);
            }
            catch { /* ignore */ }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "QQ message handling failed");
            try
            {
                await SendVisibleAsync(parsed, $"错误: {ex.Message}", CancellationToken.None).ConfigureAwait(false);
            }
            catch { /* ignore */ }
        }
    }

    private async Task HandleSubscriptionCommandAsync(
        ParsedQQMessage parsed,
        QQCommandInterceptor.CommandKind cmd,
        CancellationToken cancellationToken)
    {
        var optedOut = cmd == QQCommandInterceptor.CommandKind.Unsubscribe;
        var userId = parsed.SenderOpenId ?? "";
        if (!_adapter.HasDirectory || string.IsNullOrWhiteSpace(userId))
        {
            await SendVisibleAsync(parsed, "当前进程未接入通道目录，订阅状态未变更。", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await _adapter.SetOptOutAsync(userId, optedOut, cancellationToken).ConfigureAwait(false);
        await SendVisibleAsync(parsed, optedOut ? "已退订主动消息。" : "已恢复订阅。", cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task HandlePermissionAsync(
        GatewayEvent gatewayEvent,
        string sessionId,
        ParsedQQMessage parsed,
        CancellationToken cancellationToken)
    {
        var permissionId = gatewayEvent.Data?.PermissionId;
        if (string.IsNullOrEmpty(permissionId))
            return;

        if (!_permissionPolicy.ShouldPrompt(gatewayEvent))
        {
            await _sessionClient.RespondPermissionAsync(sessionId, permissionId, allow: true, ct: cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var requestId = Guid.NewGuid().ToString("N");
        _permissionState.Remember(requestId, sessionId, permissionId, TimeSpan.FromMinutes(10));
        var tool = gatewayEvent.Data?.ToolName ?? gatewayEvent.Data?.PermissionKind ?? "permission";
        var text = $"需要确认权限：{tool}\n{gatewayEvent.Data?.PermissionMessage}".Trim();
        var keyboard = QQPermissionCardBuilder.BuildKeyboard(requestId);
        var prepared = _adapter.ApplyPassiveWindow(parsed);
        await _qqHttp.SendTextAsync(prepared, text, keyboard, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendVisibleAsync(ParsedQQMessage parsed, string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
            text = "(空回复)";

        var result = await _adapter.SendParsedAsync(parsed, text, cancellationToken).ConfigureAwait(false);
        if (!result.Delivered)
        {
            _logger.LogWarning(
                "QQ reply failed kind={Kind} detail={Detail}",
                result.FailureKind,
                result.Detail);
        }
    }
}
