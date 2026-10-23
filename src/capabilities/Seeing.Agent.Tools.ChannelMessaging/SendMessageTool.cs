using Seeing.Agent.Abstractions.Channels;
using Seeing.Agent.Abstractions.Tools;
using System.Text.Json;

namespace Seeing.Agent.Tools.ChannelMessaging;

/// <summary>
/// 向已登记的用户或对话投递一条文本。
/// </summary>
public sealed class SendMessageTool : ITool
{
    private readonly IChannelMessageSink _sink;

    public SendMessageTool(IChannelMessageSink sink)
    {
        _sink = sink;
    }

    public string Id => "send_channel_message";

    public string Description =>
        "向已登记通道的用户或对话发送文本消息。\n\n" +
        "- 目标二选一：targetUserId，或 conversationExternalId + conversationKind。\n" +
        "- channelId 如 qq、wecom；先查目录用 `list_channel_conversations` 或 `list_channel_users`。";

    public IReadOnlyList<string> Tags => [];

    public ToolCategory Category => ToolCategory.ExternalService;

    public JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            channelId = new { type = "string", description = "通道 id，如 qq / wecom" },
            targetUserId = new { type = "string", description = "目标用户 ExternalId；与对话参数二选一" },
            conversationExternalId = new { type = "string", description = "对话 ExternalId；与 targetUserId 二选一" },
            conversationKind = new
            {
                type = "string",
                @enum = new[] { "direct", "group", "guild" },
                description = "对话种类：direct / group / guild"
            },
            text = new { type = "string", description = "要发送的文本" }
        },
        required = new[] { "channelId", "text" }
    });

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        var channelId = ToolJson.ReadString(arguments, "channelId");
        var text = ToolJson.ReadString(arguments, "text");
        var targetUserId = ToolJson.ReadString(arguments, "targetUserId");
        var conversationExternalId = ToolJson.ReadString(arguments, "conversationExternalId");
        var conversationKind = ToolJson.ReadString(arguments, "conversationKind");

        if (channelId == null)
            return Fail("缺少 channelId");
        if (text == null)
            return Fail("缺少 text");

        var hasUser = targetUserId != null;
        var hasConversation = conversationExternalId != null || conversationKind != null;
        if (hasUser == hasConversation)
            return Fail("targetUserId 与 conversationExternalId+conversationKind 必须二选一");

        var ct = context.CancellationToken;
        var content = new OutboundContent { Text = text };
        OutboundDeliveryResult result;
        if (hasUser)
        {
            result = await _sink.SendToUserAsync(channelId, targetUserId!, content, ct).ConfigureAwait(false);
        }
        else
        {
            if (conversationExternalId == null || conversationKind == null
                || !ToolJson.TryParseKind(conversationKind, out var kind))
                return Fail("conversationExternalId 与 conversationKind（direct|group|guild）必须同时提供");

            var target = new DeliveryTarget
            {
                Conversation = new ConversationInfo
                {
                    ChannelId = channelId,
                    Kind = kind,
                    ExternalId = conversationExternalId
                }
            };
            result = await _sink.SendToConversationAsync(target, content, ct).ConfigureAwait(false);
        }

        var message = ChannelDeliveryMessages.For(result);
        return new ToolResult
        {
            Success = result.Delivered,
            Title = result.Delivered ? "已投递" : "投递失败",
            Output = message,
            Error = result.Delivered ? null : message
        };
    }

    private static ToolResult Fail(string message) => new()
    {
        Success = false,
        Title = "投递失败",
        Output = message,
        Error = message
    };
}
