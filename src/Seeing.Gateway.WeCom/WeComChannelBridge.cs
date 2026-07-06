using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Gateway;
using Seeing.Gateway.Channels;
using Seeing.Gateway.Models;
using Seeing.Gateway.Client;

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
        ILogger<WeComChannelBridge> logger)
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
        _gatewayAdapter = new WebSocketGatewayClientAdapter(gatewayClient);
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

        await _gatewayClient.ConnectAsync(_cts.Token).ConfigureAwait(false);

        await _weComClient.ConnectAsync(new WeComWsClientOptions
        {
            BotId = _options.BotId,
            Secret = _options.Secret,
            WsUrl = _options.WsUrl,
            HeartbeatIntervalSeconds = _options.HeartbeatIntervalSeconds,
            MaxReconnectAttempts = _options.MaxReconnectAttempts
        }, _cts.Token).ConfigureAwait(false);

        _logger.LogInformation("WeCom Channel Bridge 已启动");
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _weComClient.OnMessage -= HandleWeComMessageAsync;
        _weComClient.OnEvent -= HandleWeComEventAsync;
        _cts?.Cancel();

        await _weComClient.DisposeAsync().ConfigureAwait(false);
        await _gatewayClient.DisposeAsync().ConfigureAwait(false);

        _cts?.Dispose();
        _cts = null;
    }

    private Task HandleWeComMessageAsync(WeComIncomingContext context, CancellationToken cancellationToken)
    {
        _ = HandleWeComMessageCoreAsync(context, cancellationToken);
        return Task.CompletedTask;
    }

    private async Task HandleWeComMessageCoreAsync(
        WeComIncomingContext context,
        CancellationToken cancellationToken)
    {
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

        if (await _commandInterceptor.TryHandleAsync(parsed, sessionId, cancellationToken).ConfigureAwait(false))
        {
            _sessionTracker.Touch(parsed);
            return;
        }

        _ = ProcessMessageAsync(parsed, sessionId, cancellationToken);
    }

    private Task HandleWeComEventAsync(WeComWsFrame frame, CancellationToken cancellationToken)
    {
        _ = HandleWeComEventCoreAsync(frame, cancellationToken);
        return Task.CompletedTask;
    }

    private async Task HandleWeComEventCoreAsync(WeComWsFrame frame, CancellationToken cancellationToken)
    {
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

    private async Task HandleTemplateCardEventAsync(
        ParsedWeComTemplateCardEvent cardEvent,
        CancellationToken cancellationToken)
    {
        if (!_permissionState.TryGetByTaskId(cardEvent.TaskId, out var pending))
        {
            _logger.LogWarning("WeCom 模板卡片事件无匹配权限: TaskId={TaskId}", cardEvent.TaskId);
            return;
        }

        bool? allow = null;
        if (_permissionPolicy.IsAllowEventKey(cardEvent.EventKey))
            allow = true;
        else if (_permissionPolicy.IsDenyEventKey(cardEvent.EventKey))
            allow = false;

        if (allow == null)
        {
            _logger.LogWarning("WeCom 未知模板卡片 event_key: {EventKey}", cardEvent.EventKey);
            return;
        }

        try
        {
            await _gatewayClient.RespondPermissionAsync(
                pending.SessionId,
                pending.PermissionId,
                allow.Value,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            _permissionState.TryRemoveByTaskId(cardEvent.TaskId, out _);

            var updateBody = WeComPermissionCardBuilder.BuildResultCard(cardEvent.TaskId, allow.Value);
            await _weComClient.ReplyUpdateTemplateCardAsync(cardEvent.Frame, updateBody, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WeCom 权限卡片响应失败: PermissionId={PermissionId}", pending.PermissionId);
        }
    }

    private async Task ProcessMessageAsync(
        ParsedWeComMessage parsed,
        string sessionId,
        CancellationToken cancellationToken)
    {
        await using var streamState = new WeComStreamState(_weComClient, parsed.Frame, _options);

        if (parsed.HasUnsupportedReply)
        {
            await streamState.CompleteAsync(parsed.UnsupportedReply, cancellationToken).ConfigureAwait(false);
            return;
        }

        await ProcessMessageCoreAsync(parsed, sessionId, streamState, cancellationToken).ConfigureAwait(false);
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
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.EffectiveProcessingMaxDurationSeconds));

            try
            {
                await foreach (var gatewayEvent in _gatewayAdapter.ChatAsync(request, timeoutCts.Token)
                                   .ConfigureAwait(false))
                {
                    var disposition = reply.Apply(gatewayEvent);

                    switch (disposition)
                    {
                        case GatewayReplyDisposition.StreamUpdated:
                            await streamState.PublishAsync(reply.Text, cancellationToken).ConfigureAwait(false);
                            break;

                        case GatewayReplyDisposition.Permission:
                            await HandlePermissionAsync(gatewayEvent, sessionId, parsed.Frame, cancellationToken)
                                .ConfigureAwait(false);
                            break;

                        case GatewayReplyDisposition.RunCompleted:
                            _logger.LogInformation(
                                "WeCom 回复完成: SessionId={SessionId}, TextLength={TextLength}",
                                sessionId,
                                reply.Text.Length);
                            await streamState.CompleteAsync(reply.Text, cancellationToken).ConfigureAwait(false);
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
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("WeCom 消息处理超时: SessionId={SessionId}", sessionId);
                await _gatewayClient.StopChatAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
                await streamState.FailAsync("处理超时，请稍后重试", CancellationToken.None).ConfigureAwait(false);
                _sessionTracker.Touch(parsed);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WeCom 消息处理失败: SessionId={SessionId}", sessionId);
            await streamState.FailAsync(ex.Message, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandlePermissionAsync(
        GatewayEvent gatewayEvent,
        string sessionId,
        WeComWsFrame requestFrame,
        CancellationToken cancellationToken)
    {
        var permissionId = gatewayEvent.Data?.PermissionId;
        if (string.IsNullOrWhiteSpace(permissionId))
            return;

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

        var taskId = $"perm_{Guid.NewGuid():N}";
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, _options.PermissionCardTtlSeconds));
        _permissionState.Register(taskId, new PendingPermissionCard
        {
            SessionId = sessionId,
            PermissionId = permissionId,
            Resource = gatewayEvent.Data?.Resource ?? string.Empty,
            PermissionKind = gatewayEvent.Data?.PermissionKind ?? string.Empty,
            ExpiresAt = expiresAt
        });

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
