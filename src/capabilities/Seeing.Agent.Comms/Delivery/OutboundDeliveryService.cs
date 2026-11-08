using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Channels;

namespace Seeing.Agent.Comms.Delivery;

/// <summary>
/// 出站投递管道：目录闸门 → 能力裁决 → 适配器 → 成功后记频控并通知。
/// <para><b>目录闸门只有一条：屏蔽。</b>目录记录「这个工作区见过哪些渠道对话」，
/// 不是可投递白名单；未登记的目标照样投递（首次主动联系、新装的会话绑定都可能没登记）。
/// 退订与频控是用户/运维语义的限制，与「是否登记」无关。</para>
/// </summary>
public class OutboundDeliveryService
{
    private readonly IChannelDirectory _directory;
    private readonly IChannelAdapterRegistry _registry;
    private readonly IChannelDeliveryNotifier _notifier;
    private readonly ILogger<OutboundDeliveryService> _logger;

    public OutboundDeliveryService(
        IChannelDirectory directory,
        IChannelAdapterRegistry registry,
        IChannelDeliveryNotifier notifier,
        ILogger<OutboundDeliveryService>? logger = null)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
        _logger = logger ?? NullLogger<OutboundDeliveryService>.Instance;
    }

    public virtual async Task<OutboundDeliveryResult> DeliverAsync(OutboundMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        // 不变式（Spec 5.5）：文本与附件至少一个非空；违反属于生产端缺陷，fail fast。
        var content = message.Content;
        if (content is null || (string.IsNullOrEmpty(content.Text) && content.Attachments is not { Count: > 0 }))
            throw new ArgumentException("出站内容必须至少包含文本或附件", nameof(message));

        var conversation = message.Target.Conversation;
        var userId = ResolveSubjectUserId(message);

        // ── 准入段 ──
        // 「没有被屏蔽就放行」是本管道的唯一目录类硬闸门：目录是**记录本**，不是白名单。
        // 出站目标由会话绑定 / 工具显式指定 / 调度画像给出，这些来源本身就是可信的——
        // 再拿「是否已登记」去拦，只会让「首次主动找某个还没来过的用户」永远失败。
        //
        // 屏蔽判定必须先于按用户判定：Direct 对话的用户条目仍在目录里，
        // 只看用户表会漏掉屏蔽。
        if (_directory.IsBlocked(conversation.ChannelId, conversation.Kind, conversation.ExternalId))
            return OutboundDeliveryResult.Fail(OutboundFailureKind.NotInDirectory, "该对话已被屏蔽");

        // 私聊（或群内定向到某人）按用户判定退订与频控；群/频道整体投递按对话判定频控。
        if (!string.IsNullOrEmpty(userId))
        {
            var user = await _directory.FindUserAsync(conversation.ChannelId, userId, ct).ConfigureAwait(false);

            // 未登记不放行判定为通过：退订是用户显式表达，只在有记录时成立。
            if (user?.OptedOut == true)
                return OutboundDeliveryResult.Fail(OutboundFailureKind.OptedOut, "用户已退订");

            if (_directory.IsRateLimited(conversation.ChannelId, userId))
                return OutboundDeliveryResult.Fail(OutboundFailureKind.RateLimited, "超出每小时投递上限");
        }
        else if (conversation.Kind is ConversationKind.Group or ConversationKind.Guild)
        {
            if (_directory.IsRateLimited(conversation.ChannelId, conversation.ExternalId))
                return OutboundDeliveryResult.Fail(OutboundFailureKind.RateLimited, "超出每小时投递上限");
        }
        else
        {
            // Direct 对话但没有任何可判定主体：这是目标构造缺陷，不是目录准入。
            return OutboundDeliveryResult.Fail(OutboundFailureKind.NotInDirectory, "缺少投递用户");
        }

        if (!_registry.TryGet(conversation.ChannelId, out var adapter) || adapter is null)
            return OutboundDeliveryResult.Fail(OutboundFailureKind.ChannelUnavailable, "通道适配器未注册");

        // 能力是派生属性（部分适配器每次新建），一次投递只读一次
        var capabilities = adapter.Capabilities;

        // 锚点：仅被动回复、通道声明支持、且消息未显式携带时，补齐目录中该对话最后一条入站消息 ID。
        var prepared = message;
        if (message.DeliveryKind == OutboundDeliveryKind.Reply
            && capabilities.SupportsReplyAnchor
            && string.IsNullOrEmpty(message.Target.ReplyToMsgId))
        {
            var anchor = _directory.ResolveReplyAnchor(conversation);
            if (!string.IsNullOrEmpty(anchor))
                prepared = message with { Target = message.Target with { ReplyToMsgId = anchor } };
        }

        // 附件能力校验：只在带附件时执行，纯文本消息不受通道附件能力限制。
        if (content.Attachments is { Count: > 0 } attachments)
        {
            var rejection = ValidateAttachments(
                capabilities, conversation.ChannelId, conversation.Kind, attachments);
            if (rejection is not null)
                return rejection;
        }

        OutboundDeliveryResult sent;
        try
        {
            sent = await adapter.SendAsync(prepared, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "通道 {ChannelId} 投递失败", conversation.ChannelId);
            return OutboundDeliveryResult.Fail(OutboundFailureKind.PlatformError, ex.Message);
        }

        if (!sent.Delivered)
        {
            return sent.FailureKind is null
                ? OutboundDeliveryResult.Fail(OutboundFailureKind.PlatformError, sent.Detail)
                : sent;
        }

        _directory.RecordDelivery(conversation.ChannelId, userId ?? conversation.ExternalId);
        await _notifier.NotifyDeliveredAsync(prepared, sent, ct).ConfigureAwait(false);
        return sent;
    }

    /// <summary>按通道能力裁决附件；通过返回 null。</summary>
    private static OutboundDeliveryResult? ValidateAttachments(
        ChannelCapabilities capabilities,
        string channelId,
        ConversationKind kind,
        IReadOnlyList<OutboundAttachment> attachments)
    {
        var limits = capabilities.ResolveAttachments(kind);
        if (limits is null)
        {
            return OutboundDeliveryResult.Fail(
                OutboundFailureKind.AttachmentUnsupported,
                $"通道 {channelId} 不支持附件（{kind} 对话）");
        }

        if (attachments.Count > limits.MaxPerMessage)
        {
            return OutboundDeliveryResult.Fail(
                OutboundFailureKind.AttachmentUnsupported,
                $"附件数量 {attachments.Count} 超过通道单条上限 {limits.MaxPerMessage}");
        }

        foreach (var attachment in attachments)
        {
            if (!limits.Supports(attachment.Kind))
            {
                return OutboundDeliveryResult.Fail(
                    OutboundFailureKind.AttachmentUnsupported,
                    $"通道 {channelId} 不支持 {attachment.Kind} 附件");
            }

            if (attachment.SizeBytes <= 0)
            {
                return OutboundDeliveryResult.Fail(
                    OutboundFailureKind.AttachmentUnavailable,
                    $"附件不可用：{attachment.LocalPath}");
            }

            if (attachment.SizeBytes > limits.MaxBytes)
            {
                return OutboundDeliveryResult.Fail(
                    OutboundFailureKind.AttachmentTooLarge,
                    $"附件 {attachment.LocalPath} 大小 {attachment.SizeBytes} 字节超过通道上限 {limits.MaxBytes} 字节");
            }
        }

        return null;
    }

    private static string? ResolveSubjectUserId(OutboundMessage message)
    {
        if (!string.IsNullOrEmpty(message.Target.RecipientUserId))
            return message.Target.RecipientUserId;

        return message.Target.Conversation.Kind == ConversationKind.Direct
            ? message.Target.Conversation.ExternalId
            : null;
    }
}
