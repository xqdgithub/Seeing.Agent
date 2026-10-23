namespace Seeing.Agent.Abstractions.Channels;

// ── ChannelCapabilities.cs ──
public sealed record ChannelAttachmentCapabilities
{
    public required IReadOnlyList<OutboundAttachmentKind> Kinds { get; init; }

    public required long MaxBytes { get; init; }

    public required int MaxPerMessage { get; init; }
}

/// <summary>按对话种类覆盖附件能力。用有序条目而非字典，避免枚举键的序列化分歧。</summary>
public sealed record ConversationAttachmentCapabilities
{
    public required ConversationKind Kind { get; init; }

    public required ChannelAttachmentCapabilities Capabilities { get; init; }
}

public sealed record ChannelCapabilities
{
    /// <summary>通道是否接受 ReplyToMsgId 作为被动回复锚点；false 时 Comms 不填锚点。</summary>
    public required bool SupportsReplyAnchor { get; init; }

    /// <summary>null = 该通道不支持附件。</summary>
    public ChannelAttachmentCapabilities? Attachments { get; init; }

    /// <summary>按对话种类覆盖；未命中回落到 <see cref="Attachments"/>。</summary>
    public IReadOnlyList<ConversationAttachmentCapabilities>? AttachmentsByConversationKind { get; init; }
}
