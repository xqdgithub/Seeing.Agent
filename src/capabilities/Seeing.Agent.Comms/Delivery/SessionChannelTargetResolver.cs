using Seeing.Agent.Abstractions.Channels;
using Seeing.Session.Core;

namespace Seeing.Agent.Comms.Delivery;

/// <summary>
/// 会话 → 出站目标：取会话自身或父链上（≤8 层）最近一条 <see cref="SessionData.ChannelRoute"/> 绑定。
/// 只读：不创建会话；空 ID 或全链无绑定返回 <see cref="OutboundFailureKind.NotInDirectory"/>。
/// </summary>
public sealed class SessionChannelTargetResolver : IChannelTargetResolver
{
    /// <summary>父链回溯上限（防止异常关系数据导致无限回溯）。</summary>
    private const int MaxParentDepth = 8;

    private const string NoBindingDetail = "当前会话及父会话均未绑定通道";

    private readonly ISessionManager _sessions;
    private readonly ISessionGroupManager _groups;

    public SessionChannelTargetResolver(ISessionManager sessions, ISessionGroupManager groups)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _groups = groups ?? throw new ArgumentNullException(nameof(groups));
    }

    public async Task<ChannelTargetResolution> ResolveSessionTargetAsync(
        string sessionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return ChannelTargetResolution.Fail(OutboundFailureKind.NotInDirectory, NoBindingDetail);

        var currentId = sessionId;
        // 自身 + 最多 8 层父会话
        for (var depth = 0; depth <= MaxParentDepth; depth++)
        {
            ct.ThrowIfCancellationRequested();

            // 不存在的会话（含父链中断）直接判定未绑定：不创建会话、不继续猜测关系。
            // 优先取内存中的活对象：对正在执行的会话调用 LoadAsync 会用磁盘副本覆盖缓存，
            // 使执行循环持有的会话实例与缓存分家，导致工具消息/终态写不回同一条会话。
            var session = await _sessions.TryGetOrLoadAsync(currentId).ConfigureAwait(false);
            if (session is null)
                break;

            if (session.ChannelRoute is { } route && !string.IsNullOrWhiteSpace(route.ChannelId))
                return ChannelTargetResolution.Ok(BuildTarget(route));

            var parentId = await _groups.GetParentAsync(currentId, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(parentId))
                break;

            currentId = parentId;
        }

        return ChannelTargetResolution.Fail(OutboundFailureKind.NotInDirectory, NoBindingDetail);
    }

    private static DeliveryTarget BuildTarget(SessionChannelRoute route)
    {
        var kind = ParseKind(route.Kind);
        return new DeliveryTarget
        {
            Conversation = new ConversationInfo
            {
                ChannelId = route.ChannelId.Trim(),
                Kind = kind,
                ExternalId = route.ExternalId,
                Title = route.Title,
            },
            // 直聊目标与用户身份同值；群/频道不填（群内定向由调用方显式指定）
            RecipientUserId = kind == ConversationKind.Direct ? route.ExternalId : null,
        };
    }

    /// <summary>路由中的 Kind 是小写稳定字符串（direct / group / guild），同时容忍枚举名。</summary>
    private static ConversationKind ParseKind(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
            return ConversationKind.Direct;

        return kind.Trim() switch
        {
            "group" => ConversationKind.Group,
            "guild" => ConversationKind.Guild,
            "direct" => ConversationKind.Direct,
            _ => Enum.TryParse<ConversationKind>(kind, ignoreCase: true, out var parsed)
                ? parsed
                : ConversationKind.Direct
        };
    }
}
