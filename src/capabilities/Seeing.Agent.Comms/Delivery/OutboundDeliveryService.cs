using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Channels;

namespace Seeing.Agent.Comms.Delivery;

/// <summary>
/// 出站投递管道：目标解析 → 准入 → 适配器 → 成功后记频控并通知。
/// </summary>
public sealed class OutboundDeliveryService
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

    public async Task<OutboundDeliveryResult> DeliverAsync(OutboundMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var conversation = message.Target.Conversation;
        var userId = ResolveSubjectUserId(message);
        if (string.IsNullOrEmpty(userId))
            return OutboundDeliveryResult.Fail(OutboundFailureKind.NotInDirectory, "缺少投递用户");

        var user = await _directory.FindUserAsync(conversation.ChannelId, userId, ct).ConfigureAwait(false);
        if (user is null)
            return OutboundDeliveryResult.Fail(OutboundFailureKind.NotInDirectory, "用户不在目录中");

        if (user.OptedOut)
            return OutboundDeliveryResult.Fail(OutboundFailureKind.OptedOut, "用户已退订");

        if (_directory.IsRateLimited(conversation.ChannelId, userId))
            return OutboundDeliveryResult.Fail(OutboundFailureKind.RateLimited, "超出每小时投递上限");

        if (!_registry.TryGet(conversation.ChannelId, out var adapter) || adapter is null)
            return OutboundDeliveryResult.Fail(OutboundFailureKind.ChannelUnavailable, "通道适配器未注册");

        OutboundDeliveryResult sent;
        try
        {
            sent = await adapter.SendAsync(message, ct).ConfigureAwait(false);
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

        _directory.RecordDelivery(conversation.ChannelId, userId);
        await _notifier.NotifyDeliveredAsync(message, sent, ct).ConfigureAwait(false);
        return sent;
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
