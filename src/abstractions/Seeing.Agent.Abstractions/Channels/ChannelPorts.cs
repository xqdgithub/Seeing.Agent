namespace Seeing.Agent.Abstractions.Channels;

// ── ChannelPorts.cs ──
public interface IChannelDirectoryStore          // Store：纯存取
{
    Task SaveUserAsync(ChannelUserEntry user, CancellationToken ct = default);
    Task SaveConversationAsync(ConversationEntry entry, CancellationToken ct = default);
    Task<ChannelUserEntry?> FindUserAsync(string channelId, string externalId, CancellationToken ct = default);
    Task<ConversationEntry?> FindConversationAsync(string channelId, ConversationKind kind, string externalId, CancellationToken ct = default);
    Task<IReadOnlyList<ChannelUserEntry>> ListUsersAsync(string channelId, int skip, int take, CancellationToken ct = default);
    Task<IReadOnlyList<ConversationEntry>> ListConversationsAsync(string channelId, int skip, int take, CancellationToken ct = default);
    Task LoadIntoAsync(Action<ChannelUserEntry, ConversationEntry?> sink, CancellationToken ct = default);  // 启动载入

    /// <summary>删除用户条目（已不存在的键视为成功）。</summary>
    Task DeleteUserAsync(string channelId, string externalId, CancellationToken ct = default);

    /// <summary>删除对话条目（已不存在的键视为成功）。</summary>
    Task DeleteConversationAsync(string channelId, ConversationKind kind, string externalId, CancellationToken ct = default);

    /// <summary>屏蔽名单（启动载入，用于压住「收到消息即登记」）。</summary>
    Task<IReadOnlyList<BlockedConversation>> ListBlockedConversationsAsync(CancellationToken ct = default);

    Task SaveBlockedConversationAsync(BlockedConversation entry, CancellationToken ct = default);

    Task DeleteBlockedConversationAsync(string channelId, ConversationKind kind, string externalId, CancellationToken ct = default);
}

public interface IChannelDirectory              // Manager：登记/查询/退订/频控
{
    /// <summary>
    /// 入站准入 + 登记：判断该对话是否可以处理，可则登记（用户/对话/成员/锚点）。
    /// <para><b>这是入站唯一入口</b>——调用方必须先取结果、再决定是否继续处理：
    /// <c>Accepted == false</c>（如对话已被屏蔽）时<b>不得</b>把消息交给 agent。</para>
    /// <para>命名刻意用 <c>Admit</c> 而非 <c>Register</c>：它是决策，不只是副作用。</para>
    /// </summary>
    InboundAdmission AdmitInbound(ChannelInboundMessage message);

    /// <summary>该对话最近一条入站消息 ID；从未登记返回 null。有效性由适配器判定。</summary>
    string? ResolveReplyAnchor(ConversationInfo conversation);

    Task<IReadOnlyList<ChannelUserEntry>> ListUsersAsync(string channelId, int skip = 0, int take = 50, CancellationToken ct = default);
    Task<IReadOnlyList<ConversationEntry>> ListConversationsAsync(string channelId, int skip = 0, int take = 50, CancellationToken ct = default);

    /// <summary>已出现会话的渠道 ID 集合（目录维度枚举，绑定选择器数据源）</summary>
    Task<IReadOnlyList<string>> ListChannelIdsAsync(CancellationToken ct = default);

    Task<ChannelUserEntry?> FindUserAsync(string channelId, string externalId, CancellationToken ct = default);
    Task<ConversationEntry?> FindConversationAsync(string channelId, ConversationKind kind, string externalId, CancellationToken ct = default);
    Task SetOptOutAsync(string channelId, string externalId, bool optedOut, CancellationToken ct = default);
    bool IsRateLimited(string channelId, string externalId);   // 滑窗判定（已投递计数）
    void RecordDelivery(string channelId, string externalId);  // 投递成功后计数

    /// <summary>
    /// 修改对话显示名（<see cref="ConversationInfo.Title"/>）。仅影响展示与选择器文案，
    /// 不改变 <see cref="ConversationInfo.Kind"/> / <see cref="ConversationInfo.ExternalId"/>——
    /// 后两者是平台身份，改了就无法投递。
    /// </summary>
    Task RenameConversationAsync(
        string channelId, ConversationKind kind, string externalId, string? title, CancellationToken ct = default);

    /// <summary>删除对话目录条目。该对话若仍在入站活跃，下一条消息会重新登记（要彻底拦住用屏蔽）。</summary>
    Task DeleteConversationAsync(
        string channelId, ConversationKind kind, string externalId, CancellationToken ct = default);

    /// <summary>删除用户目录条目。Direct 对话与出站准入都会因此失效（对方再次来消息会重新登记）。</summary>
    Task DeleteUserAsync(string channelId, string externalId, CancellationToken ct = default);

    /// <summary>屏蔽对话：从目录移除并加入屏蔽名单，之后入站不再登记。幂等。</summary>
    Task BlockConversationAsync(
        string channelId, ConversationKind kind, string externalId, string? reason = null, CancellationToken ct = default);

    /// <summary>解除屏蔽；该对话之后收到消息会重新登记。</summary>
    Task UnblockConversationAsync(
        string channelId, ConversationKind kind, string externalId, CancellationToken ct = default);

    /// <summary>屏蔽名单快照。</summary>
    Task<IReadOnlyList<BlockedConversation>> ListBlockedConversationsAsync(CancellationToken ct = default);

    /// <summary>该对话当前是否处于屏蔽状态。</summary>
    bool IsBlocked(string channelId, ConversationKind kind, string externalId);
}

public interface IChannelMessageSink            // Sink：agent 工具唯一单向出口
{
    Task<OutboundDeliveryResult> SendToUserAsync(
        string channelId, string userExternalId, OutboundContent content, CancellationToken ct = default);

    Task<OutboundDeliveryResult> SendToConversationAsync(
        DeliveryTarget target, OutboundContent content, CancellationToken ct = default);
}

/// <summary>会话 → 出站目标解析（Sink 不承担路由职责）。</summary>
public interface IChannelTargetResolver
{
    Task<ChannelTargetResolution> ResolveSessionTargetAsync(string sessionId, CancellationToken ct = default);
}

public sealed record ChannelTargetResolution
{
    public DeliveryTarget? Target { get; init; }
    public OutboundFailureKind? FailureKind { get; init; }
    public string? Detail { get; init; }

    public static ChannelTargetResolution Ok(DeliveryTarget target) => new() { Target = target };

    public static ChannelTargetResolution Fail(OutboundFailureKind kind, string detail) =>
        new() { FailureKind = kind, Detail = detail };
}

public interface IChannelAdapter                // Provider：通道适配器
{
    string ChannelId { get; }

    /// <summary>能力声明；所有实现必须显式提供（不支持附件时 Attachments = null）。</summary>
    ChannelCapabilities Capabilities { get; }

    Task<OutboundDeliveryResult> SendAsync(OutboundMessage message, CancellationToken ct = default);
}

public interface IChannelAdapterRegistry        // Registry：单实例 per channel
{
    void Register(IChannelAdapter adapter);     // 同 ChannelId 重复注册：后注册者生效 + 记 Warning
    bool TryGet(string channelId, out IChannelAdapter? adapter);
    IReadOnlyList<string> RegisteredChannels { get; }
    void Unregister(string channelId);
}

public interface IChannelDeliveryNotifier       // Sink：投递通知出口（默认 no-op；Server 实现）
{
    Task NotifyDeliveredAsync(OutboundMessage message, OutboundDeliveryResult result, CancellationToken ct = default);
}
