using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Gateway;
using Seeing.Gateway.Channels;
using Seeing.Gateway.Client;
using Seeing.Gateway.Models;
using Seeing.Gateway.QQ.Connection;

namespace Seeing.Gateway.QQ;

/// <summary>
/// QQ Channel Bridge：QQ Bot WS/HTTP ↔ Gateway WebSocket
/// </summary>
public sealed class QQChannelBridge : IChannelBridge, IAsyncDisposable
{
    private readonly QQOptions _options;
    private readonly GatewayClientCommonOptions _commonOptions;
    private readonly QQWebSocketClient _qqWs;
    private readonly QQHttpApiClient _qqHttp;
    private readonly WebSocketGatewayClient _gatewayClient;
    private readonly WebSocketGatewayClientFacade _gatewayFacade;
    private readonly QQMediaFetcher _mediaFetcher;
    private readonly QQPermissionPolicy _permissionPolicy;
    private readonly QQPermissionState _permissionState;
    private readonly QQSessionTracker _sessionTracker;
    private readonly QQCommandInterceptor _commandInterceptor;
    private readonly QQPermissionResponder _permissionResponder;
    private readonly QQChannelHealth _health;
    private readonly IHostApplicationLifetime? _hostLifetime;
    private readonly ILogger<QQChannelBridge> _logger;
    private readonly ConcurrentDictionary<string, byte> _processedMessageIds = new();
    private CancellationTokenSource? _cts;

    public QQChannelBridge(
        IOptions<QQOptions> options,
        IOptions<GatewayClientCommonOptions> commonOptions,
        QQWebSocketClient qqWs,
        QQHttpApiClient qqHttp,
        WebSocketGatewayClient gatewayClient,
        QQMediaFetcher mediaFetcher,
        QQPermissionPolicy permissionPolicy,
        QQPermissionState permissionState,
        QQSessionTracker sessionTracker,
        QQCommandInterceptor commandInterceptor,
        QQPermissionResponder permissionResponder,
        QQChannelHealth health,
        ILogger<QQChannelBridge> logger,
        IHostApplicationLifetime? hostLifetime = null)
    {
        _options = options.Value;
        _commonOptions = commonOptions.Value;
        _qqWs = qqWs;
        _qqHttp = qqHttp;
        _gatewayClient = gatewayClient;
        _gatewayFacade = new WebSocketGatewayClientFacade(gatewayClient);
        _mediaFetcher = mediaFetcher;
        _permissionPolicy = permissionPolicy;
        _permissionState = permissionState;
        _sessionTracker = sessionTracker;
        _commandInterceptor = commandInterceptor;
        _permissionResponder = permissionResponder;
        _health = health;
        _logger = logger;
        _hostLifetime = hostLifetime;
    }

    public string ChannelId => "qq";

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
        await _gatewayClient.ConnectAsync(_cts.Token).ConfigureAwait(false);
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
        _cts?.Cancel();
        await _qqWs.StopAsync().ConfigureAwait(false);
        await _gatewayClient.DisposeAsync().ConfigureAwait(false);
        _cts?.Dispose();
        _cts = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

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

        // 忽略带 BotPrefix 的自身回显，避免环路
        if (!string.IsNullOrEmpty(_options.BotPrefix)
            && parsed.Text.StartsWith(_options.BotPrefix, StringComparison.Ordinal))
        {
            _logger.LogInformation("QQ skip BotPrefix echo {MsgId} text={Text}", parsed.MsgId, parsed.Text);
            return;
        }

        // 忽略状态 Ack 回声（群聊偶发把机器人回复推回）
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

        _logger.LogInformation(
            "QQ recv {Type} from={Sender} group={Group} text={Text}",
            parsed.MessageType,
            parsed.SenderOpenId,
            parsed.GroupOpenId,
            parsed.Text.Length > 80 ? parsed.Text[..80] : parsed.Text);

        _ = ProcessMessageAsync(parsed, cancellationToken);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    private async Task ProcessMessageAsync(ParsedQQMessage parsed, CancellationToken cancellationToken)
    {
        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts?.Token ?? CancellationToken.None);
        jobCts.CancelAfter(TimeSpan.FromSeconds(_options.EffectiveProcessingMaxDurationSeconds));
        var jobToken = jobCts.Token;

        try
        {
            var sessionId = _sessionTracker.Resolve(parsed);
            var cmd = _commandInterceptor.TryParse(parsed.Text, out _);
            if (cmd == QQCommandInterceptor.CommandKind.New)
            {
                sessionId = _sessionTracker.Rotate(parsed);
                await _qqHttp.SendTextAsync(parsed, "已开启新对话。", cancellationToken: jobToken).ConfigureAwait(false);
                return;
            }

            if (cmd == QQCommandInterceptor.CommandKind.Clear)
            {
                await _gatewayClient.ResetSessionAsync(sessionId, jobToken).ConfigureAwait(false);
                await _qqHttp.SendTextAsync(parsed, "已清空当前上下文。", cancellationToken: jobToken).ConfigureAwait(false);
                return;
            }

            // 非命令：先回状态，再 Submit（异步，不阻塞主路径）
            var ackText = _options.EffectiveAckMessage;
            if (ackText != null)
            {
                var ackTarget = parsed;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _qqHttp.SendStatusAsync(ackTarget, ackText, CancellationToken.None)
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
            string? executionId = null;

            var submit = await _gatewayFacade.SubmitAsync(request, jobToken).ConfigureAwait(false);
            if (!submit.Success || string.IsNullOrEmpty(submit.ExecutionId))
            {
                await _qqHttp.SendTextAsync(parsed, submit.Error ?? "提交失败", cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);
                return;
            }

            executionId = submit.ExecutionId;
            await foreach (var gatewayEvent in _gatewayFacade.SubscribeAsync(sessionId, executionId, jobToken)
                               .ConfigureAwait(false))
            {
                var disposition = reply.Apply(gatewayEvent);
                switch (disposition)
                {
                    case GatewayReplyDisposition.Permission:
                        await HandlePermissionAsync(gatewayEvent, sessionId, parsed, jobToken).ConfigureAwait(false);
                        break;
                    case GatewayReplyDisposition.RunCompleted:
                        await SendFinalAsync(parsed, reply.Text, jobToken).ConfigureAwait(false);
                        _sessionTracker.Touch(parsed);
                        return;
                    case GatewayReplyDisposition.RunCancelled:
                        await SendFinalAsync(parsed, $"⚠️ {reply.TerminalMessage}", CancellationToken.None).ConfigureAwait(false);
                        return;
                    case GatewayReplyDisposition.RunFailed:
                        await SendFinalAsync(parsed, reply.TerminalMessage ?? "执行失败", CancellationToken.None).ConfigureAwait(false);
                        return;
                }
            }

            if (!reply.IsTerminal)
                await SendFinalAsync(parsed, reply.Text, jobToken).ConfigureAwait(false);

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
                await _qqHttp.SendTextAsync(parsed, "处理超时，请稍后重试", cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch { /* ignore */ }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "QQ message handling failed");
            try
            {
                await _qqHttp.SendTextAsync(parsed, $"错误: {ex.Message}", cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch { /* ignore */ }
        }
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
            await _gatewayClient.RespondPermissionAsync(sessionId, permissionId, allow: true, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var requestId = Guid.NewGuid().ToString("N");
        _permissionState.Remember(requestId, sessionId, permissionId, TimeSpan.FromMinutes(10));
        var tool = gatewayEvent.Data?.ToolName ?? gatewayEvent.Data?.PermissionKind ?? "permission";
        var text = $"需要确认权限：{tool}\n{gatewayEvent.Data?.PermissionMessage}".Trim();
        var keyboard = QQPermissionCardBuilder.BuildKeyboard(requestId);
        await _qqHttp.SendTextAsync(parsed, text, keyboard, cancellationToken).ConfigureAwait(false);
    }

    private Task SendFinalAsync(ParsedQQMessage parsed, string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
            text = "(空回复)";
        return _qqHttp.SendReplyAsync(parsed, text, cancellationToken: cancellationToken);
    }
}
