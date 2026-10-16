namespace Seeing.Session.Core;

/// <summary>
/// 会话出站绑定。镜像 <c>ConversationInfo</c>，但本包不引用 Abstractions。
/// </summary>
public sealed record SessionChannelRoute
{
    public required string ChannelId { get; init; }

    /// <summary>对话种类的稳定字符串（direct / group / guild）。</summary>
    public required string Kind { get; init; }

    public required string ExternalId { get; init; }

    public string? Title { get; init; }
}
