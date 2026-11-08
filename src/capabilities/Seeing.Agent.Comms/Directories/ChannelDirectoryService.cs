using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Channels;

namespace Seeing.Agent.Comms.Directories;

/// <summary>
/// 渠道目录管理器：准入/登记/退订/频控/屏蔽，以及目录的人工维护。
/// <para><b>不持有用户与对话的内存副本</b>：读写一律走 <see cref="IChannelDirectoryStore"/>。
/// 目录文件由多进程共享（WebUI 主进程 / GatewayServer / ChannelHost 桥），
/// 服务层再缓存一份就会出现「某个进程写入了、本进程永远看不到」——那是页面显示空目录的根因。</para>
/// <para>只保留两类进程内状态：<b>回复锚点</b>（入站登记的副产物，无需跨进程一致）
/// 与<b>频控滑窗</b>（投递计数，天然是进程内语义）。</para>
/// </summary>
public sealed class ChannelDirectoryService : IChannelDirectory
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);

    private readonly IChannelDirectoryStore _store;
    private readonly IOptions<CommsOptions> _options;
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _replyAnchors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<DateTimeOffset>> _deliveries = new(StringComparer.Ordinal);

    public ChannelDirectoryService(IChannelDirectoryStore store, IOptions<CommsOptions> options)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public InboundAdmission AdmitInbound(ChannelInboundMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        // 准入判定（决策）与登记（副作用）在同一处，调用方无法「只登记、不问准入」。
        if (IsBlocked(message.Conversation.ChannelId, message.Conversation.Kind, message.Conversation.ExternalId))
            return InboundAdmission.Reject(InboundRejectionReason.Blocked);

        var now = DateTimeOffset.UtcNow;

        // 登记需要「已有的 FirstSeenAt / OptedOut / 成员 / Title」，从 store 读当前状态，
        // 在内存里算出 upsert 结果再写回（store 负责落盘与跨进程合并）。
        var existingUser = _store
            .FindUserAsync(message.Sender.ChannelId, message.Sender.ExternalId)
            .GetAwaiter().GetResult();
        var existingConversation = _store
            .FindConversationAsync(
                message.Conversation.ChannelId,
                message.Conversation.Kind,
                message.Conversation.ExternalId)
            .GetAwaiter().GetResult();

        var user = BuildUser(message, existingUser, now);
        var conversation = BuildConversation(message, existingConversation, now);

        lock (_gate)
        {
            // 锚点属于「入站登记」的自然产物：每对话只保留最后一条，不做 TTL
            if (!string.IsNullOrWhiteSpace(message.ReplyToMsgId))
                _replyAnchors[ChannelKeys.Conversation(message.Conversation)] = message.ReplyToMsgId;
        }

        _store.SaveUserAsync(user).GetAwaiter().GetResult();
        _store.SaveConversationAsync(conversation).GetAwaiter().GetResult();
        return InboundAdmission.Accept();
    }

    /// <inheritdoc />
    public string? ResolveReplyAnchor(ConversationInfo conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        lock (_gate)
        {
            return _replyAnchors.TryGetValue(ChannelKeys.Conversation(conversation), out var msgId)
                ? msgId
                : null;
        }
    }

    public Task<IReadOnlyList<ChannelUserEntry>> ListUsersAsync(
        string channelId, int skip = 0, int take = 50, CancellationToken ct = default)
        => _store.ListUsersAsync(channelId, skip, take, ct);

    public Task<IReadOnlyList<ConversationEntry>> ListConversationsAsync(
        string channelId, int skip = 0, int take = 50, CancellationToken ct = default)
        => _store.ListConversationsAsync(channelId, skip, take, ct);

    public Task<IReadOnlyList<string>> ListChannelIdsAsync(CancellationToken ct = default)
        => _store.ListChannelIdsAsync(ct);

    public Task<ChannelUserEntry?> FindUserAsync(string channelId, string externalId, CancellationToken ct = default)
        => _store.FindUserAsync(channelId, externalId, ct);

    public Task<ConversationEntry?> FindConversationAsync(
        string channelId, ConversationKind kind, string externalId, CancellationToken ct = default)
        => _store.FindConversationAsync(channelId, kind, externalId, ct);

    public Task SetOptOutAsync(string channelId, string externalId, bool optedOut, CancellationToken ct = default)
    {
        var existing = _store.FindUserAsync(channelId, externalId, ct).GetAwaiter().GetResult();
        if (existing is null)
        {
            // 从未见过该用户：只在「撤销退订」时无需建条目（退订对未知用户无意义）。
            if (!optedOut)
                return Task.CompletedTask;

            var now = DateTimeOffset.UtcNow;
            return _store.SaveUserAsync(new ChannelUserEntry
            {
                ChannelId = channelId,
                ExternalId = externalId,
                FirstSeenAt = now,
                LastSeenAt = now,
                OptedOut = true
            }, ct);
        }

        return _store.SaveUserAsync(existing with { OptedOut = optedOut }, ct);
    }

    public bool IsRateLimited(string channelId, string externalId)
    {
        var limit = _options.Value.RateLimitPerHour;
        if (limit <= 0)
            return false;

        lock (_gate)
        {
            if (!_deliveries.TryGetValue(ChannelKeys.User(channelId, externalId), out var queue))
                return false;

            Prune(queue, DateTimeOffset.UtcNow);
            return queue.Count >= limit;
        }
    }

    public void RecordDelivery(string channelId, string externalId)
    {
        var limit = _options.Value.RateLimitPerHour;
        if (limit <= 0)
            return;

        lock (_gate)
        {
            var key = ChannelKeys.User(channelId, externalId);
            if (!_deliveries.TryGetValue(key, out var queue))
            {
                queue = new Queue<DateTimeOffset>();
                _deliveries[key] = queue;
            }

            var now = DateTimeOffset.UtcNow;
            queue.Enqueue(now);
            Prune(queue, now);
        }
    }

    public Task RenameConversationAsync(
        string channelId, ConversationKind kind, string externalId, string? title, CancellationToken ct = default)
    {
        var existing = _store
            .FindConversationAsync(channelId, kind, externalId, ct)
            .GetAwaiter().GetResult();
        if (existing is null)
            return Task.CompletedTask;

        return _store.SaveConversationAsync(existing with
        {
            Conversation = existing.Conversation with
            {
                Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim()
            }
        }, ct);
    }

    public Task DeleteConversationAsync(
        string channelId, ConversationKind kind, string externalId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            // 锚点属于该对话：一并清掉，避免删除后仍被当作被动回复目标。
            _replyAnchors.Remove(ChannelKeys.Conversation(channelId, kind, externalId));
        }

        return _store.DeleteConversationAsync(channelId, kind, externalId, ct);
    }

    public Task DeleteUserAsync(string channelId, string externalId, CancellationToken ct = default)
        => _store.DeleteUserAsync(channelId, externalId, ct);

    public async Task BlockConversationAsync(
        string channelId,
        ConversationKind kind,
        string externalId,
        string? reason = null,
        CancellationToken ct = default)
    {
        var existing = await _store
            .FindConversationAsync(channelId, kind, externalId, ct)
            .ConfigureAwait(false);

        lock (_gate)
            _replyAnchors.Remove(ChannelKeys.Conversation(channelId, kind, externalId));

        await _store.DeleteConversationAsync(channelId, kind, externalId, ct).ConfigureAwait(false);

        // 已屏蔽时保留原 BlockedAt（重复屏蔽不重置时间）。
        var previous = await _store.ListBlockedConversationsAsync(ct).ConfigureAwait(false);
        var blockedAt = previous
            .FirstOrDefault(b => string.Equals(b.ChannelId, channelId, StringComparison.Ordinal)
                && b.Kind == kind
                && string.Equals(b.ExternalId, externalId, StringComparison.Ordinal))
            ?.BlockedAt ?? DateTimeOffset.UtcNow;

        await _store.SaveBlockedConversationAsync(new BlockedConversation
        {
            ChannelId = channelId,
            Kind = kind,
            ExternalId = externalId,
            Title = existing?.Conversation.Title,
            BlockedAt = blockedAt,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()
        }, ct).ConfigureAwait(false);
    }

    public Task UnblockConversationAsync(
        string channelId, ConversationKind kind, string externalId, CancellationToken ct = default)
        => _store.DeleteBlockedConversationAsync(channelId, kind, externalId, ct);

    public Task<IReadOnlyList<BlockedConversation>> ListBlockedConversationsAsync(CancellationToken ct = default)
        => _store.ListBlockedConversationsAsync(ct);

    public bool IsBlocked(string channelId, ConversationKind kind, string externalId)
        => _store.IsBlocked(channelId, kind, externalId);

    /// <summary>
    /// 合并入站消息与既有用户条目：保留 FirstSeenAt / OptedOut / DisplayName（空值不覆盖）。
    /// </summary>
    private static ChannelUserEntry BuildUser(
        ChannelInboundMessage message,
        ChannelUserEntry? existing,
        DateTimeOffset now)
    {
        var sender = message.Sender;
        return new ChannelUserEntry
        {
            ChannelId = sender.ChannelId,
            ExternalId = sender.ExternalId,
            DisplayName = string.IsNullOrWhiteSpace(sender.DisplayName) ? existing?.DisplayName : sender.DisplayName,
            FirstSeenAt = existing?.FirstSeenAt ?? now,
            LastSeenAt = now,
            OptedOut = existing?.OptedOut ?? false,
        };
    }

    /// <summary>
    /// 合并入站消息与既有对话条目：保留 FirstSeenAt、成员并集、Title（空值不覆盖）。
    /// </summary>
    private static ConversationEntry BuildConversation(
        ChannelInboundMessage message,
        ConversationEntry? existing,
        DateTimeOffset now)
    {
        var info = message.Conversation;
        var members = existing?.MemberExternalIds.ToList() ?? [];
        if (!members.Contains(message.Sender.ExternalId, StringComparer.Ordinal))
            members.Add(message.Sender.ExternalId);

        return new ConversationEntry
        {
            Conversation = new ConversationInfo
            {
                ChannelId = info.ChannelId,
                Kind = info.Kind,
                ExternalId = info.ExternalId,
                // 入站 Title 只在既有为空时覆盖：用户手改的显示名不该被下一条消息抹掉。
                Title = string.IsNullOrWhiteSpace(info.Title) ? existing?.Conversation.Title : info.Title,
            },
            FirstSeenAt = existing?.FirstSeenAt ?? now,
            LastSeenAt = now,
            MemberExternalIds = members,
        };
    }

    private static void Prune(Queue<DateTimeOffset> queue, DateTimeOffset now)
    {
        var cutoff = now - Window;
        while (queue.Count > 0 && queue.Peek() <= cutoff)
            queue.Dequeue();
    }
}
