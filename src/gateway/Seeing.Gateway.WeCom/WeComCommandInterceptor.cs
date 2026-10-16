using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Gateway.Client;
using Seeing.Gateway.Models;

namespace Seeing.Gateway.WeCom;

/// <summary>
/// 拦截企微斜杠命令（/new、/clear、/subscribe、/unsubscribe），不转发 Agent。
/// </summary>
public sealed class WeComCommandInterceptor
{
    private readonly WeComOptions _options;
    private readonly WeComSessionTracker _sessionTracker;
    private readonly IGatewayChannelHostClient _hostClient;
    private readonly WeComChannelAdapter _adapter;
    private readonly WeComAibotWsClient _weComClient;
    private readonly ILogger<WeComCommandInterceptor> _logger;

    public WeComCommandInterceptor(
        IOptions<WeComOptions> options,
        WeComSessionTracker sessionTracker,
        IGatewayChannelHostClient hostClient,
        WeComChannelAdapter adapter,
        WeComAibotWsClient weComClient,
        ILogger<WeComCommandInterceptor> logger)
    {
        _options = options.Value;
        _sessionTracker = sessionTracker;
        _hostClient = hostClient;
        _adapter = adapter;
        _weComClient = weComClient;
        _logger = logger;
    }

    /// <summary>若命中命令则处理并返回 true</summary>
    public async Task<bool> TryHandleAsync(
        ParsedWeComMessage parsed,
        string currentSessionId,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseCommand(parsed, out var command))
            return false;

        _logger.LogInformation(
            "WeCom 命令: {Command}, SessionId={SessionId}, UserId={UserId}",
            command,
            currentSessionId,
            parsed.UserId);

        string reply;
        try
        {
            reply = command switch
            {
                WeComConversationCommand.Clear => await HandleClearAsync(currentSessionId, cancellationToken)
                    .ConfigureAwait(false),
                WeComConversationCommand.New => HandleNew(parsed),
                WeComConversationCommand.Subscribe => await HandleSubscriptionAsync(parsed, optedOut: false, cancellationToken)
                    .ConfigureAwait(false),
                WeComConversationCommand.Unsubscribe => await HandleSubscriptionAsync(parsed, optedOut: true, cancellationToken)
                    .ConfigureAwait(false),
                _ => string.Empty
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WeCom 命令处理失败: {Command}, SessionId={SessionId}", command, currentSessionId);
            reply = "❌ 命令执行失败，请稍后重试。";
        }

        await ReplyTextAsync(parsed.Frame, reply, cancellationToken).ConfigureAwait(false);
        return true;
    }

    internal static bool TryParseCommand(ParsedWeComMessage parsed, out WeComConversationCommand command)
    {
        command = default;
        var text = ExtractCommandText(parsed);
        if (string.IsNullOrWhiteSpace(text) || !text.StartsWith('/'))
            return false;

        var stripped = text.Trim().TrimStart('/');
        var cmd = stripped.Split([' ', '\t', '\n'], 2, StringSplitOptions.RemoveEmptyEntries)[0]
            .ToLowerInvariant();

        return cmd switch
        {
            "clear" => Assign(WeComConversationCommand.Clear, out command),
            "new" => Assign(WeComConversationCommand.New, out command),
            "subscribe" => Assign(WeComConversationCommand.Subscribe, out command),
            "unsubscribe" => Assign(WeComConversationCommand.Unsubscribe, out command),
            _ => false
        };
    }

    internal static string? ToCommandToken(WeComConversationCommand command) => command switch
    {
        WeComConversationCommand.New => "new",
        WeComConversationCommand.Clear => "clear",
        WeComConversationCommand.Subscribe => "subscribe",
        WeComConversationCommand.Unsubscribe => "unsubscribe",
        _ => null
    };

    internal static string ExtractCommandText(ParsedWeComMessage parsed)
    {
        var text = parsed.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            foreach (var part in parsed.InputParts)
            {
                if (part is GatewayTextContentPart textPart && !string.IsNullOrWhiteSpace(textPart.Text))
                {
                    text = textPart.Text;
                    break;
                }
            }
        }

        text = text.Trim();
        return WeComGroupMentionNormalizer.NormalizeUserText(text, parsed.ChatType);
    }

    private async Task<string> HandleClearAsync(string sessionId, CancellationToken cancellationToken)
    {
        try
        {
            await _hostClient.ResetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
            return "✅ 上下文已清空，可以继续对话。";
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Session not found", StringComparison.Ordinal))
        {
            // 用户尚未与 Agent 对话时 Gateway 侧无会话文件，视为已清空。
            return "✅ 上下文已清空，可以继续对话。";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WeCom /clear: Gateway 重置失败: SessionId={SessionId}", sessionId);
            return "⚠️ 清空请求失败，请确认 Gateway 已连接后重试。";
        }
    }

    private string HandleNew(ParsedWeComMessage parsed)
    {
        var newSessionId = _sessionTracker.RotateSession(parsed, reason: "command_new");
        return $"✅ 已开启新对话。\n会话 ID: {newSessionId}";
    }

    private async Task<string> HandleSubscriptionAsync(
        ParsedWeComMessage parsed,
        bool optedOut,
        CancellationToken cancellationToken)
    {
        var userId = parsed.UserId;
        if (!_adapter.HasDirectory || string.IsNullOrWhiteSpace(userId))
            return "当前进程未接入通道目录，订阅状态未变更。";

        await _adapter.SetOptOutAsync(userId, optedOut, cancellationToken).ConfigureAwait(false);
        return optedOut ? "已退订主动消息。" : "已恢复订阅。";
    }

    private async Task ReplyTextAsync(WeComWsFrame frame, string text, CancellationToken cancellationToken)
    {
        await using var streamState = new WeComStreamState(_weComClient, frame, _options);
        await streamState.SendInstantAsync(text, cancellationToken).ConfigureAwait(false);
    }

    private static bool Assign(WeComConversationCommand value, out WeComConversationCommand command)
    {
        command = value;
        return true;
    }
}

internal enum WeComConversationCommand
{
    Clear,
    New,
    Subscribe,
    Unsubscribe
}
