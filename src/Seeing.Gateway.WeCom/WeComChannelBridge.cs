using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Gateway;
using Seeing.Gateway.Channels;
using Seeing.Gateway.Models;
using Seeing.Gateway.Client;
using Seeing.Gateway.WeCom.Connection;

namespace Seeing.Gateway.WeCom;

/// <summary>
/// 企微 Channel Bridge：企微 WebSocket ↔ Gateway WebSocket
/// </summary>
public sealed class WeComChannelBridge : IChannelBridge, IAsyncDisposable
{
    private readonly WeComOptions _options;
    private readonly GatewayClientCommonOptions _commonOptions;
    private readonly WeComAibotWsClient _weComClient;
    private readonly WebSocketGatewayClient _gatewayClient;
    private readonly WeComMediaFetcher _mediaFetcher;
    private readonly WeComPermissionPolicy _permissionPolicy;
    private readonly WeComPermissionState _permissionState;
    private readonly WeComSessionTracker _sessionTracker;
    private readonly WeComCommandInterceptor _commandInterceptor;
    private readonly WeComPermissionResponder _permissionResponder;
    private readonly WeComActiveStreamRegistry _activeStreams;
    private readonly IHostApplicationLifetime? _hostLifetime;
    private readonly ILogger<WeComChannelBridge> _logger;

    private readonly ConcurrentDictionary<string, byte> _processedMessageIds = new();
    private readonly WebSocketGatewayClientAdapter _gatewayAdapter;
    private CancellationTokenSource? _cts;

    public WeComChannelBridge(
        IOptions<WeComOptions> options,
        IOptions<GatewayClientCommonOptions> commonOptions,
        WeComAibotWsClient weComClient,
        WebSocketGatewayClient gatewayClient,
        WeComMediaFetcher mediaFetcher,
        WeComPermissionPolicy permissionPolicy,
        WeComPermissionState permissionState,
        WeComSessionTracker sessionTracker,
        WeComCommandInterceptor commandInterceptor,
        WeComPermissionResponder permissionResponder,
        WeComActiveStreamRegistry activeStreams,
        ILogger<WeComChannelBridge> logger,
        IHostApplicationLifetime? hostLifetime = null)
    {
        _options = options.Value;
        _commonOptions = commonOptions.Value;
        _weComClient = weComClient;
        _gatewayClient = gatewayClient;
        _mediaFetcher = mediaFetcher;
        _permissionPolicy = permissionPolicy;
        _permissionState = permissionState;
        _sessionTracker = sessionTracker;
        _commandInterceptor = commandInterceptor;
        _permissionResponder = permissionResponder;
        _activeStreams = activeStreams;
        _gatewayAdapter = new WebSocketGatewayClientAdapter(gatewayClient);
        _hostLifetime = hostLifetime;
        _logger = logger;
    }

    public string ChannelId => "wecom";

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("WeCom Channel 未启用");
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.BotId) || string.IsNullOrWhiteSpace(_options.Secret))
            throw new InvalidOperationException("WeCom BotId 和 Secret 必须配置");

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _weComClient.OnMessage += HandleWeComMessageAsync;
        _weComClient.OnEvent += HandleWeComEventAsync;
        _weComClient.ConnectionChanged += HandleConnectionChanged;

        await _gatewayClient.ConnectAsync(_cts.Token).ConfigureAwait(false);

        await _weComClient.ConnectAsync(new WeComWsClientOptions
        {
            BotId = _options.BotId,
            Secret = _options.Secret,
            WsUrl = _options.WsUrl,
            HeartbeatIntervalSeconds = _options.EffectiveHeartbeatIntervalSeconds,
            MaxReconnectAttempts = _options.MaxReconnectAttempts
        }, _cts.Token).ConfigureAwait(false);

        _logger.LogInformation("WeCom Channel Bridge 已启动");
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _weComClient.OnMessage -= HandleWeComMessageAsync;
        _weComClient.OnEvent -= HandleWeComEventAsync;
        _weComClient.ConnectionChanged -= HandleConnectionChanged;

        await _activeStreams.AbortAllAsync("Bridge 正在停止", CancellationToken.None).ConfigureAwait(false);
        _cts?.Cancel();

        await _weComClient.DisposeAsync().ConfigureAwait(false);
        await _gatewayClient.DisposeAsync().ConfigureAwait(false);

        _cts?.Dispose();
        _cts = null;
    }

    private void HandleConnectionChanged(object? sender, WeComConnectionChangedEventArgs e)
    {
        if (IsTransientDisconnect(e))
        {
            _logger.LogWarning(
                "WeCom 连接 transient 断开: {Previous} → {Current}, epoch={Epoch}, reason={Reason}, connected={ConnectedSeconds:F1}s",
                e.PreviousState,
                e.CurrentState,
                e.Epoch,
                e.Reason,
                e.ConnectedDuration?.TotalSeconds);

            _ = _activeStreams.PauseAllAsync(CancellationToken.None);
        }
        else if (e.CurrentState == WeComConnectionState.Superseded)
        {
            _logger.LogWarning(
                "WeCom 连接被取代: epoch={Epoch}, reason={Reason}",
                e.Epoch,
                e.Reason);

            _ = _activeStreams.AbortAllAsync("连接已被其他实例取代", CancellationToken.None);
        }
        else if (e.CurrentState == WeComConnectionState.Active)
        {
            _logger.LogInformation("WeCom 连接已恢复 (epoch={Epoch})", e.Epoch);
            _ = _activeStreams.FlushAllAsync(CancellationToken.None);
        }
        else if (e.CurrentState == WeComConnectionState.Failed)
        {
            _logger.LogCritical(
                "WeCom 连接无法恢复 (epoch={Epoch}, reason={Reason})，Channel 即将停止",
                e.Epoch,
                e.Reason);

            _ = _activeStreams.AbortAllAsync("连接无法恢复", CancellationToken.None);
            _hostLifetime?.StopApplication();
        }
    }

    private static bool IsTransientDisconnect(WeComConnectionChangedEventArgs e) =>
        e.PreviousState is WeComConnectionState.Subscribed or WeComConnectionState.Active
        && e.CurrentState is WeComConnectionState.Stopping
            or WeComConnectionState.Disconnected
            or WeComConnectionState.Backoff;

    private Task HandleWeComMessageAsync(WeComIncomingContext context, CancellationToken cancellationToken)
    {
        _ = HandleWeComMessageCoreAsync(context, cancellationToken);
        return Task.CompletedTask;
    }

    private async Task HandleWeComMessageCoreAsync(
        WeComIncomingContext context,
        CancellationToken cancellationToken)
    {
        if (WeComMessageParser.TryParseStreamRefresh(context, out var streamId) && streamId != null)
        {
            if (_activeStreams.TryHandleRefresh(context.Frame, streamId, cancellationToken))
                return;

            _logger.LogDebug("WeCom 流式刷新无匹配 stream: {StreamId}", streamId);
            return;
        }

        var (ok, parsed) = await WeComMessageParser.TryParseAsync(
                context,
                _mediaFetcher,
                _logger,
                cancellationToken)
            .ConfigureAwait(false);
        if (!ok || parsed == null)
            return;

        if (IsDuplicate(parsed.MessageId))
            return;

        var sessionId = _sessionTracker.ResolveSessionId(parsed);
        _logger.LogInformation(
            "WeCom 收到消息: UserId={UserId}, SessionId={SessionId}, Parts={PartCount}",
            parsed.UserId,
            sessionId,
            parsed.InputParts.Count);

        if (await _permissionResponder.TryHandleTextReplyAsync(parsed, sessionId, cancellationToken).ConfigureAwait(false))
        {
            _sessionTracker.Touch(parsed);
            return;
        }

        if (await _commandInterceptor.TryHandleAsync(parsed, sessionId, cancellationToken).ConfigureAwait(false))
        {
            _sessionTracker.Touch(parsed);
            return;
        }

        _ = ProcessMessageAsync(parsed, sessionId);
    }

    private Task HandleWeComEventAsync(WeComWsFrame frame, CancellationToken cancellationToken)
    {
        _ = HandleWeComEventCoreAsync(frame, cancellationToken);
        return Task.CompletedTask;
    }

    private async Task HandleWeComEventCoreAsync(WeComWsFrame frame, CancellationToken cancellationToken)
    {
        if (WeComEventParser.TryParseDisconnectedEvent(frame, out var disconnected) && disconnected != null)
        {
            _logger.LogInformation(
                "WeCom Bridge 收到 disconnected_event: MessageId={MessageId}, AiBotId={AiBotId}",
                disconnected.MessageId,
                disconnected.AiBotId);
            return;
        }

        if (WeComEventParser.TryParseEnterChat(frame, out var enterChat) && enterChat != null)
        {
            await HandleEnterChatAsync(enterChat, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (WeComEventParser.TryParseTemplateCardEvent(frame, out var cardEvent) && cardEvent != null)
            await HandleTemplateCardEventAsync(cardEvent, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleEnterChatAsync(ParsedWeComEnterChat enterChat, CancellationToken cancellationToken)
    {
        if (_options.ResetOnEnterChatWhenIdle && _sessionTracker.IsIdle(enterChat))
        {
            var newSessionId = _sessionTracker.RotateSession(enterChat, reason: "enter_chat_idle");
            _logger.LogInformation(
                "WeCom enter_chat 超时重置: UserId={UserId}, SessionId={SessionId}",
                enterChat.UserId,
                newSessionId);
        }

        var welcomeText = _options.GetEffectiveWelcomeText();
        _logger.LogInformation("WeCom enter_chat: UserId={UserId}", enterChat.UserId);

        try
        {
            await _weComClient.ReplyWelcomeAsync(enterChat.Frame, welcomeText, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WeCom 欢迎语发送失败: UserId={UserId}", enterChat.UserId);
        }
    }

    private Task HandleTemplateCardEventAsync(
        ParsedWeComTemplateCardEvent cardEvent,
        CancellationToken cancellationToken)
    {
        _ = HandleTemplateCardEventCoreAsync(cardEvent, cancellationToken);
        return Task.CompletedTask;
    }

    private async Task HandleTemplateCardEventCoreAsync(
        ParsedWeComTemplateCardEvent cardEvent,
        CancellationToken cancellationToken)
    {
        try
        {
            await _permissionResponder.TryHandleTemplateCardAsync(cardEvent, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WeCom 模板卡片权限处理失败: TaskId={TaskId}", cardEvent.TaskId);
        }
    }

    private async Task ProcessMessageAsync(
        ParsedWeComMessage parsed,
        string sessionId)
    {
        if (_cts == null)
            return;

        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        jobCts.CancelAfter(TimeSpan.FromSeconds(_options.EffectiveProcessingMaxDurationSeconds));
        var jobToken = jobCts.Token;

        await using var streamState = new WeComStreamState(_weComClient, parsed.Frame, _options, _activeStreams);

        if (parsed.HasUnsupportedReply)
        {
            await streamState.CompleteAsync(parsed.UnsupportedReply, jobToken).ConfigureAwait(false);
            return;
        }

        await ProcessMessageCoreAsync(parsed, sessionId, streamState, jobToken).ConfigureAwait(false);
    }

    private async Task ProcessMessageCoreAsync(
        ParsedWeComMessage parsed,
        string sessionId,
        WeComStreamState streamState,
        CancellationToken cancellationToken)
    {
        try
        {
            await streamState.BeginAsync(cancellationToken).ConfigureAwait(false);

            var request = WeComMessageParser.ToGatewayRequest(
                parsed,
                sessionId,
                _commonOptions.Agent,
                _commonOptions.Mode,
                _commonOptions.Model);

            var reply = new GatewayAssistantReplyCollector();

            try
            {
                await foreach (var gatewayEvent in _gatewayAdapter.ChatAsync(request, cancellationToken)
                                   .ConfigureAwait(false))
                {
                    var disposition = reply.Apply(gatewayEvent);

                    switch (disposition)
                    {
                        case GatewayReplyDisposition.StreamUpdated:
                        case GatewayReplyDisposition.MessageSnapshot:
                            await streamState.PublishAsync(reply.Text, cancellationToken).ConfigureAwait(false);
                            break;

                        case GatewayReplyDisposition.Permission:
                            await HandlePermissionAsync(
                                    gatewayEvent,
                                    sessionId,
                                    parsed.Frame,
                                    streamState,
                                    cancellationToken)
                                .ConfigureAwait(false);
                            break;

                        case GatewayReplyDisposition.RunCompleted:
                            await streamState.CompleteAsync(reply.Text, cancellationToken).ConfigureAwait(false);
                            _logger.LogInformation(
                                "WeCom 回复完成: SessionId={SessionId}, TextLength={TextLength}, ReconnectWaits={ReconnectWaits}, Epoch={Epoch}",
                                sessionId,
                                reply.Text.Length,
                                streamState.ReconnectWaits,
                                _weComClient.ConnectionEpoch);
                            _sessionTracker.Touch(parsed);
                            return;

                        case GatewayReplyDisposition.RunCancelled:
                            await streamState.CompleteAsync($"⚠️ {reply.TerminalMessage}", cancellationToken)
                                .ConfigureAwait(false);
                            _sessionTracker.Touch(parsed);
                            return;

                        case GatewayReplyDisposition.RunFailed:
                            await streamState.FailAsync(reply.TerminalMessage ?? "Agent 执行失败", cancellationToken)
                                .ConfigureAwait(false);
                            _sessionTracker.Touch(parsed);
                            return;
                    }
                }

                if (!reply.IsTerminal)
                {
                    await streamState.CompleteAsync(reply.Text, cancellationToken).ConfigureAwait(false);
                }

                _sessionTracker.Touch(parsed);
            }
            catch (OperationCanceledException) when (_cts?.IsCancellationRequested == true)
            {
                _logger.LogDebug("WeCom 消息处理已取消 (Bridge 停止): SessionId={SessionId}", sessionId);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("WeCom 消息处理超时: SessionId={SessionId}", sessionId);
                await _gatewayClient.StopChatAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
                await streamState.FailAsync("处理超时，请稍后重试", CancellationToken.None).ConfigureAwait(false);
                _sessionTracker.Touch(parsed);
            }
        }
        catch (OperationCanceledException) when (_cts?.IsCancellationRequested == true)
        {
            _logger.LogDebug("WeCom 消息处理已取消 (Bridge 停止): SessionId={SessionId}", sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WeCom 消息处理失败: SessionId={SessionId}", sessionId);
            try
            {
                await streamState.FailAsync(ex.Message, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception failEx)
            {
                _logger.LogWarning(failEx, "WeCom 失败回复发送失败: SessionId={SessionId}", sessionId);
            }
        }
    }

    private async Task HandlePermissionAsync(
        GatewayEvent gatewayEvent,
        string sessionId,
        WeComWsFrame requestFrame,
        WeComStreamState streamState,
        CancellationToken cancellationToken)
    {
        var permissionId = gatewayEvent.Data?.PermissionId;
        if (string.IsNullOrWhiteSpace(permissionId))
            return;

        if (gatewayEvent.Status != GatewayEventStatus.InProgress
            || !string.IsNullOrWhiteSpace(gatewayEvent.Data?.PermissionDecision))
        {
            return;
        }

        _logger.LogInformation(
            "WeCom 权限请求: {PermissionId} {Kind} {Resource} Risk={Risk}",
            permissionId,
            gatewayEvent.Data?.PermissionKind,
            gatewayEvent.Data?.Resource,
            gatewayEvent.Data?.RiskLevel);

        if (!_permissionPolicy.ShouldPromptUser(gatewayEvent))
        {
            await _gatewayClient.RespondPermissionAsync(
                sessionId,
                permissionId,
                allow: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return;
        }

        _permissionState.CleanupExpired();
        _permissionState.TryRemoveByPermissionId(permissionId, out _);

        var expiresAt = DateTimeOffset.Now.AddSeconds(_options.EffectivePermissionCardTtlSeconds);
        var pending = new PendingPermissionCard
        {
            SessionId = sessionId,
            PermissionId = permissionId,
            Resource = gatewayEvent.Data?.Resource ?? string.Empty,
            PermissionKind = gatewayEvent.Data?.PermissionKind ?? string.Empty,
            ExpiresAt = expiresAt
        };

        // 流式回复进行中：同一 req_id 只能走 stream，发 template_card 会占用被动回复槽导致最终正文无法展示。
        if (streamState.IsStreamOpen)
        {
            _permissionState.Register(pending);
            var notice = WeComPermissionResponder.BuildStreamPrompt(gatewayEvent.Data);
            await streamState.PublishPermissionNoticeAsync(notice, cancellationToken).ConfigureAwait(false);
            return;
        }

        var taskId = $"perm_{Guid.NewGuid():N}";
        _permissionState.Register(new PendingPermissionCard
        {
            SessionId = sessionId,
            PermissionId = permissionId,
            Resource = gatewayEvent.Data?.Resource ?? string.Empty,
            PermissionKind = gatewayEvent.Data?.PermissionKind ?? string.Empty,
            ExpiresAt = expiresAt,
            TaskId = taskId
        }, taskId);

        var title = $"权限确认：{gatewayEvent.Data?.PermissionKind ?? "操作"}";
        var description = gatewayEvent.Data?.PermissionMessage
            ?? gatewayEvent.Data?.Resource
            ?? "Agent 请求执行一项操作，请确认是否允许。";

        var card = WeComPermissionCardBuilder.BuildPromptCard(
            taskId,
            title,
            description,
            gatewayEvent.Data?.Resource ?? string.Empty);

        await _weComClient.ReplyTemplateCardAsync(requestFrame, card, cancellationToken).ConfigureAwait(false);
    }

    private bool IsDuplicate(string messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId))
            return false;

        if (!_processedMessageIds.TryAdd(messageId, 0))
            return true;

        while (_processedMessageIds.Count > 2000)
        {
            foreach (var key in _processedMessageIds.Keys)
            {
                if (_processedMessageIds.TryRemove(key, out _))
                    break;
            }
        }

        return false;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
