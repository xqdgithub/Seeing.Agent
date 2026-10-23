namespace Seeing.Agent.Abstractions.Channels;

// ── ChannelModels.cs ──
public enum ConversationKind { Direct, Group, Guild }

public sealed record ChannelUserIdentity
{
    public required string ChannelId { get; init; }        // "qq" / "wecom"
    public required string ExternalId { get; init; }       // union openid / wecom userid
    public string? DisplayName { get; init; }
}

public sealed record ConversationInfo
{
    public required string ChannelId { get; init; }
    public required ConversationKind Kind { get; init; }
    public required string ExternalId { get; init; }       // Direct=用户 ExternalId；Group=group_openid；Guild=channel_id
    public string? Title { get; init; }
}

public enum OutboundDeliveryKind { Reply, Push, SystemNotice }

public sealed record DeliveryTarget
{
    public required ConversationInfo Conversation { get; init; }
    public string? RecipientUserId { get; init; }          // 群内定向/@（ChannelUserIdentity.ExternalId）
    public string? ReplyToMsgId { get; init; }             // 被动回复窗口内携带
}

public sealed record OutboundMessage
{
    public required DeliveryTarget Target { get; init; }
    public required OutboundDeliveryKind DeliveryKind { get; init; }
    public required OutboundContent Content { get; init; }
    public string? Source { get; init; }                   // "scheduler.cron" / "tool.send_message"
    public string? CorrelationSessionId { get; init; }
    public string? JobId { get; init; }
}

public enum OutboundFailureKind
{
    NotInDirectory,
    OptedOut,
    RateLimited,
    ChannelUnavailable,
    PlatformError,

    /// <summary>通道不支持附件 / 该类型 / 超过单条数量。</summary>
    AttachmentUnsupported,

    /// <summary>超过通道单文件上限。</summary>
    AttachmentTooLarge,

    /// <summary>路径不存在 / 是目录 / 空文件 / 不可读。</summary>
    AttachmentUnavailable
}

public sealed record OutboundDeliveryResult
{
    public required bool Delivered { get; init; }
    public OutboundFailureKind? FailureKind { get; init; }
    public string? Detail { get; init; }
    public static OutboundDeliveryResult Ok() => new() { Delivered = true };
    public static OutboundDeliveryResult Fail(OutboundFailureKind kind, string? detail = null)
        => new() { Delivered = false, FailureKind = kind, Detail = detail };
}

public sealed record ChannelUserEntry
{
    public required string ChannelId { get; init; }
    public required string ExternalId { get; init; }
    public string? DisplayName { get; init; }
    public required DateTimeOffset FirstSeenAt { get; init; }
    public required DateTimeOffset LastSeenAt { get; init; }
    public required bool OptedOut { get; init; }
}

public sealed record ConversationEntry
{
    public required ConversationInfo Conversation { get; init; }
    public required DateTimeOffset FirstSeenAt { get; init; }
    public required DateTimeOffset LastSeenAt { get; init; }
    public required IReadOnlyList<string> MemberExternalIds { get; init; }  // Group 场景惰性发现
}
