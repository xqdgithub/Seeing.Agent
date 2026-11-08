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

/// <summary>
/// 入站准入结果。由 <see cref="IChannelDirectory.AdmitInbound"/> 返回：
/// 它同时是「要不要处理这条消息」的<b>决策</b>与「登记进目录」的<b>副作用</b>。
/// </summary>
public sealed record InboundAdmission
{
    public required bool Accepted { get; init; }

    /// <summary>被拒时的原因；<see cref="Accepted"/> 为 true 时必为 null。</summary>
    public InboundRejectionReason? Reason { get; init; }

    public static InboundAdmission Accept() => new() { Accepted = true };

    public static InboundAdmission Reject(InboundRejectionReason reason) =>
        new() { Accepted = false, Reason = reason };
}

/// <summary>入站被拒原因。</summary>
public enum InboundRejectionReason
{
    /// <summary>该对话在屏蔽名单内（管理员显式屏蔽）。</summary>
    Blocked
}

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
    /// <summary>目标不可达。目前唯一的来源是<b>该对话已被屏蔽</b>；目录未登记不算拒投理由。</summary>
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

/// <summary>
/// 被屏蔽的对话：目录不再登记它，也不会出站投递。
/// <para>「删除」与「屏蔽」的区别就在这里——删除只是从当前目录移除，
/// 对方再发一条消息就会被 <see cref="IChannelDirectory.AdmitInbound"/> 重新登记；
/// 屏蔽则持久生效（双向：入站被拒、出站拒投），需手动解除。</para>
/// </summary>
public sealed record BlockedConversation
{
    public required string ChannelId { get; init; }
    public required ConversationKind Kind { get; init; }
    public required string ExternalId { get; init; }

    /// <summary>屏蔽前的显示名（便于回看「我到底屏蔽了谁」）。</summary>
    public string? Title { get; init; }

    public required DateTimeOffset BlockedAt { get; init; }

    public string? Reason { get; init; }

    /// <summary>投影回 <see cref="ConversationInfo"/>（不含 Title 以外的元数据）。</summary>
    public ConversationInfo ToConversationInfo() => new()
    {
        ChannelId = ChannelId,
        Kind = Kind,
        ExternalId = ExternalId,
        Title = Title
    };
}
