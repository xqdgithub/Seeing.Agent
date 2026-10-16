using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Runtime;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Session.Core;
using System.Text.Json;

namespace Seeing.Agent.Tools.Session;

/// <summary>
/// 会话投递工具 — 向目标会话投递一条人类身份入站消息（默认落盘并激活消费）。
/// </summary>
public sealed class SessionInboxSendTool : SessionToolBase
{
    private readonly ISessionRuntime _runtime;

    /// <summary>创建 SessionInboxSendTool 实例。</summary>
    public SessionInboxSendTool(
        ILogger<SessionInboxSendTool> logger,
        ISessionManager sessions,
        ISessionGroupManager groups,
        ISessionRuntime runtime) : base(logger, sessions, groups)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    /// <inheritdoc />
    public override string Id => "session_inbox_send";

    /// <inheritdoc />
    public override string Description =>
        "向目标会话投递一条入站消息（人类身份）。delivery=wake（默认）落盘并激活消费，" +
        "inject 仅落盘不激活。目标范围默认同组 + 父链；跨组需权限审批。";

    /// <inheritdoc />
    public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            session_id = new { type = "string", description = "目标会话 ID（必填）" },
            body = new { type = "string", description = "投递的消息正文（必填）" },
            delivery = new
            {
                type = "string",
                @enum = new[] { "wake", "inject" },
                description = "激活语义：wake=落盘并激活消费（默认）；inject=仅落盘不激活"
            }
        },
        required = new[] { "session_id", "body" }
    });

    /// <inheritdoc />
    public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        var ct = context.CancellationToken;

        var sessionId = GetStringArgument(arguments, "session_id");
        if (string.IsNullOrWhiteSpace(sessionId))
            return Failure("session_id 参数是必需的");

        var body = GetStringArgument(arguments, "body");
        if (string.IsNullOrWhiteSpace(body))
            return Failure("body 参数是必需的");

        var deliveryRaw = GetStringArgument(arguments, "delivery") ?? "wake";
        var delivery = deliveryRaw.ToLowerInvariant() switch
        {
            "wake" => InboxDelivery.Wake,
            "inject" => InboxDelivery.Inject,
            _ => (InboxDelivery?)null,
        };
        if (delivery is null)
            return Failure($"未知的 delivery: {deliveryRaw}。可选值：wake、inject");

        var targetId = sessionId!;
        if (!await IsInScopeAsync(context.SessionId, targetId, ct).ConfigureAwait(false))
        {
            var denied = await AuthorizeAsync(
                context,
                Id,
                $"向会话 {targetId} 投递消息需要授权",
                new Dictionary<string, object> { ["target_session_id"] = targetId },
                ct).ConfigureAwait(false);
            if (denied is not null)
                return denied;
        }

        SessionData target;
        try
        {
            target = await Sessions.GetOrLoadAsync(targetId, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return Failure($"会话不存在: {targetId}");
        }

        var result = await _runtime.DeliverAsync(new InboxMessage
        {
            TargetSessionId = targetId,
            Sender = new InboxSender { Kind = InboxSenderKind.Human },
            Body = body,
            Delivery = delivery.Value,
            Presentation = InboxPresentation.User,
        }, ct).ConfigureAwait(false);

        if (!result.Success)
            return Failure(result.Error ?? "会话投递失败");

        return Success(
            $"已向会话 {targetId} 投递消息（{deliveryRaw}）。",
            new Dictionary<string, object>
            {
                ["target_session_id"] = targetId,
                ["delivery"] = deliveryRaw,
                ["message_id"] = result.MessageId!,
                ["pending_input_count"] = result.PendingInputCount,
            });
    }
}
