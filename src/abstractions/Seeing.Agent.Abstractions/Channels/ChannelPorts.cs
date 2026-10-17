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
}

public interface IChannelDirectory              // Manager：登记/查询/退订/频控
{
    void RegisterInbound(ChannelInboundMessage message);   // 提交方显式调用；入站登记不清除 OptedOut
    Task<IReadOnlyList<ChannelUserEntry>> ListUsersAsync(string channelId, int skip = 0, int take = 50, CancellationToken ct = default);
    Task<IReadOnlyList<ConversationEntry>> ListConversationsAsync(string channelId, int skip = 0, int take = 50, CancellationToken ct = default);

    /// <summary>已出现会话的渠道 ID 集合（目录维度枚举，绑定选择器数据源）</summary>
    Task<IReadOnlyList<string>> ListChannelIdsAsync(CancellationToken ct = default);

    Task<ChannelUserEntry?> FindUserAsync(string channelId, string externalId, CancellationToken ct = default);
    Task<ConversationEntry?> FindConversationAsync(string channelId, ConversationKind kind, string externalId, CancellationToken ct = default);
    Task SetOptOutAsync(string channelId, string externalId, bool optedOut, CancellationToken ct = default);
    bool IsRateLimited(string channelId, string externalId);   // 滑窗判定（已投递计数）
    void RecordDelivery(string channelId, string externalId);  // 投递成功后计数
}

public interface IChannelMessageSink            // Sink：agent 工具唯一单向出口
{
    Task<OutboundDeliveryResult> SendToUserAsync(string channelId, string userExternalId, string text, CancellationToken ct = default);
    Task<OutboundDeliveryResult> SendToConversationAsync(DeliveryTarget target, string text, CancellationToken ct = default);
}

public interface IChannelAdapter                // Provider：通道适配器
{
    string ChannelId { get; }
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
