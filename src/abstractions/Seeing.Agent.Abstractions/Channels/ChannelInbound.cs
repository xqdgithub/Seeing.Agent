namespace Seeing.Agent.Abstractions.Channels;

// ── ChannelInbound.cs ──
public sealed record ChannelInboundMessage
{
    public required ChannelUserIdentity Sender { get; init; }
    public required ConversationInfo Conversation { get; init; }
    public string? ReplyToMsgId { get; init; }
    public required string SessionId { get; init; }        // 适配器命名的会话 ID（仅命名，不承载路由）
    public required string Text { get; init; }
    public string? CommandToken { get; init; }             // "new"|"clear"|"subscribe"|"unsubscribe"|null
    public IReadOnlyList<object>? AttachmentParts { get; init; }  // 媒体部件由适配器转 GatewayContentPart（T4 定型）
}
