using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Gateway.Client;
using Seeing.Gateway.Models;

namespace Seeing.Gateway.WeCom;

/// <summary>
/// 拦截企微斜杠命令（/clear、/new），不转发 Agent。
/// </summary>
public sealed partial class WeComCommandInterceptor
{
    private readonly WeComOptions _options;
    private readonly WeComSessionTracker _sessionTracker;
    private readonly WebSocketGatewayClient _gatewayClient;
    private readonly WeComAibotWsClient _weComClient;
    private readonly ILogger<WeComCommandInterceptor> _logger;

    public WeComCommandInterceptor(
        IOptions<WeComOptions> options,
        WeComSessionTracker sessionTracker,
        WebSocketGatewayClient gatewayClient,
        WeComAibotWsClient weComClient,
        ILogger<WeComCommandInterceptor> logger)
    {
        _options = options.Value;
        _sessionTracker = sessionTracker;
        _gatewayClient = gatewayClient;
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

        var reply = command switch
        {
            WeComConversationCommand.Clear => await HandleClearAsync(currentSessionId, cancellationToken)
                .ConfigureAwait(false),
            WeComConversationCommand.New => HandleNew(parsed),
            _ => string.Empty
        };

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
            _ => false
        };
    }

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
        if (string.Equals(parsed.ChatType, "group", StringComparison.OrdinalIgnoreCase))
        {
            text = GroupMentionBeforeSlash().Replace(text, string.Empty).Trim();
            if (text.StartsWith('/'))
                text = GroupMentionAfterSlash().Replace(text, string.Empty).Trim();
        }

        return text;
    }

    private async Task<string> HandleClearAsync(string sessionId, CancellationToken cancellationToken)
    {
        await _gatewayClient.ResetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return "✅ 上下文已清空，可以继续对话。";
    }

    private string HandleNew(ParsedWeComMessage parsed)
    {
        var newSessionId = _sessionTracker.RotateSession(parsed, reason: "command_new");
        return $"✅ 已开启新对话。\n会话 ID: {newSessionId}";
    }

    private async Task ReplyTextAsync(WeComWsFrame frame, string text, CancellationToken cancellationToken)
    {
        await using var streamState = new WeComStreamState(_weComClient, frame, _options);
        await streamState.FinishAsync(text, cancellationToken).ConfigureAwait(false);
    }

    private static bool Assign(WeComConversationCommand value, out WeComConversationCommand command)
    {
        command = value;
        return true;
    }

    [GeneratedRegex(@"^@\S+\s+(?=/)", RegexOptions.CultureInvariant)]
    private static partial Regex GroupMentionBeforeSlash();

    [GeneratedRegex(@"@\S+$", RegexOptions.CultureInvariant)]
    private static partial Regex GroupMentionAfterSlash();
}

internal enum WeComConversationCommand
{
    Clear,
    New
}
