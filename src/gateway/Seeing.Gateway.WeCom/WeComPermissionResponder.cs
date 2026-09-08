using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Gateway.Client;
using Seeing.Gateway.Models;

namespace Seeing.Gateway.WeCom;

/// <summary>
/// 将企微侧用户决策回传给 Gateway，并维护本地 pending 映射。
/// </summary>
public sealed class WeComPermissionResponder
{
    private readonly WeComOptions _options;
    private readonly WeComPermissionPolicy _permissionPolicy;
    private readonly WebSocketGatewayClient _gatewayClient;
    private readonly WeComPermissionState _permissionState;
    private readonly WeComAibotWsClient _weComClient;
    private readonly ILogger<WeComPermissionResponder> _logger;

    public WeComPermissionResponder(
        IOptions<WeComOptions> options,
        WeComPermissionPolicy permissionPolicy,
        WebSocketGatewayClient gatewayClient,
        WeComPermissionState permissionState,
        WeComAibotWsClient weComClient,
        ILogger<WeComPermissionResponder> logger)
    {
        _options = options.Value;
        _permissionPolicy = permissionPolicy;
        _gatewayClient = gatewayClient;
        _permissionState = permissionState;
        _weComClient = weComClient;
        _logger = logger;
    }

    public async Task<bool> TryHandleTextReplyAsync(
        ParsedWeComMessage parsed,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var text = WeComCommandInterceptor.ExtractCommandText(parsed);
        if (!TryParsePermissionReply(text, out var allow))
            return false;

        if (!_permissionState.TryGetLatestPendingForSession(sessionId, out var pending))
        {
            await ReplyInstantAsync(
                parsed.Frame,
                allow ? "当前没有待批准的权限请求。" : "当前没有待处理的权限请求。",
                cancellationToken).ConfigureAwait(false);
            return true;
        }

        var ok = await RespondAsync(pending, allow, cancellationToken).ConfigureAwait(false);
        var reply = ok
            ? (allow ? "✅ 已批准，Agent 将继续执行。" : "🚫 已拒绝，操作已取消。")
            : "⚠️ 权限请求已过期或已处理，请重新发起对话。";

        await ReplyInstantAsync(parsed.Frame, reply, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> TryHandleTemplateCardAsync(
        ParsedWeComTemplateCardEvent cardEvent,
        CancellationToken cancellationToken = default)
    {
        if (!_permissionState.TryGetByTaskId(cardEvent.TaskId, out var pending))
        {
            _logger.LogWarning("WeCom 模板卡片事件无匹配权限: TaskId={TaskId}", cardEvent.TaskId);
            await TryUpdateExpiredCardAsync(cardEvent, cancellationToken).ConfigureAwait(false);
            return false;
        }

        bool? allow = null;
        if (_permissionPolicy.IsAllowEventKey(cardEvent.EventKey))
            allow = true;
        else if (_permissionPolicy.IsDenyEventKey(cardEvent.EventKey))
            allow = false;

        if (allow == null)
        {
            _logger.LogWarning("WeCom 未知模板卡片 event_key: {EventKey}", cardEvent.EventKey);
            return false;
        }

        var ok = await RespondAsync(pending, allow.Value, cancellationToken).ConfigureAwait(false);
        if (!ok)
        {
            await TryUpdateExpiredCardAsync(cardEvent, cancellationToken).ConfigureAwait(false);
            return false;
        }

        _permissionState.TryRemoveByTaskId(cardEvent.TaskId, out _);

        var updateBody = WeComPermissionCardBuilder.BuildResultCard(cardEvent.TaskId, allow.Value);
        await _weComClient.ReplyUpdateTemplateCardAsync(cardEvent.Frame, updateBody, cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    public async Task<bool> RespondAsync(
        PendingPermissionCard pending,
        bool allow,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _gatewayClient.RespondPermissionAsync(
                pending.SessionId,
                pending.PermissionId,
                allow,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!result.Success)
            {
                _logger.LogWarning(
                    "WeCom 权限响应失败: PermissionId={PermissionId}, Error={Error}",
                    pending.PermissionId,
                    result.Error);
                return false;
            }

            _permissionState.TryRemoveByPermissionId(pending.PermissionId, out _);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WeCom 权限响应异常: PermissionId={PermissionId}", pending.PermissionId);
            return false;
        }
    }

    internal static bool TryParsePermissionReply(string text, out bool allow)
    {
        allow = false;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var normalized = text.Trim();
        if (normalized.StartsWith('/'))
            return false;

        normalized = normalized.ToLowerInvariant();

        if (normalized is "批准" or "allow" or "approve" or "confirm" or "yes" or "y" or "确认" or "同意")
        {
            allow = true;
            return true;
        }

        if (normalized is "拒绝" or "deny" or "reject" or "no" or "n" or "取消")
        {
            allow = false;
            return true;
        }

        return false;
    }

    internal static string BuildStreamPrompt(GatewayEventData? data)
    {
        var kind = data?.PermissionKind ?? "操作";
        var resource = data?.Resource ?? "-";
        var message = data?.PermissionMessage ?? "Agent 请求执行一项操作，请确认是否允许。";

        return "⏸️ 需要您的确认\n\n"
            + $"类型：{kind}\n"
            + $"资源：{resource}\n\n"
            + $"{message}\n\n"
            + "请回复「批准」或「拒绝」";
    }

    private async Task TryUpdateExpiredCardAsync(
        ParsedWeComTemplateCardEvent cardEvent,
        CancellationToken cancellationToken)
    {
        try
        {
            var updateBody = WeComPermissionCardBuilder.BuildResultCard(
                cardEvent.TaskId,
                allowed: false,
                reason: "权限已过期或已处理");
            await _weComClient.ReplyUpdateTemplateCardAsync(cardEvent.Frame, updateBody, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "WeCom 过期权限卡片更新失败: TaskId={TaskId}", cardEvent.TaskId);
        }
    }

    private async Task ReplyInstantAsync(
        WeComWsFrame frame,
        string text,
        CancellationToken cancellationToken)
    {
        await using var streamState = new WeComStreamState(_weComClient, frame, _options);
        await streamState.SendInstantAsync(text, cancellationToken).ConfigureAwait(false);
    }
}
