using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Channels;

namespace Seeing.Agent.Comms.Directories;

/// <summary>
/// 渠道目录：入站登记、退订与每用户滑窗频控。入站登记不清除 <see cref="ChannelUserEntry.OptedOut"/>。
/// </summary>
public sealed class ChannelDirectoryService : IChannelDirectory
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);

    private readonly IChannelDirectoryStore _store;
    private readonly IOptions<CommsOptions> _options;
    private readonly object _gate = new();
    private readonly Dictionary<string, ChannelUserEntry> _users = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ConversationEntry> _conversations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<DateTimeOffset>> _deliveries = new(StringComparer.Ordinal);

    public ChannelDirectoryService(IChannelDirectoryStore store, IOptions<CommsOptions> options)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _store.LoadIntoAsync(Absorb, CancellationToken.None).GetAwaiter().GetResult();
    }

    public void RegisterInbound(ChannelInboundMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var now = DateTimeOffset.UtcNow;
        ChannelUserEntry user;
        ConversationEntry conversation;
        lock (_gate)
        {
            user = UpsertUser(message, now);
            conversation = UpsertConversation(message, now);
        }

        _store.SaveUserAsync(user).GetAwaiter().GetResult();
        _store.SaveConversationAsync(conversation).GetAwaiter().GetResult();
    }

    public Task<IReadOnlyList<ChannelUserEntry>> ListUsersAsync(
        string channelId, int skip = 0, int take = 50, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IReadOnlyList<ChannelUserEntry> page = Page(
                _users.Values.Where(u => u.ChannelId == channelId).OrderBy(u => u.ExternalId, StringComparer.Ordinal),
                skip,
                take);
            return Task.FromResult(page);
        }
    }

    public Task<IReadOnlyList<ConversationEntry>> ListConversationsAsync(
        string channelId, int skip = 0, int take = 50, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IReadOnlyList<ConversationEntry> page = Page(
                _conversations.Values
                    .Where(c => c.Conversation.ChannelId == channelId)
                    .OrderBy(c => c.Conversation.ExternalId, StringComparer.Ordinal),
                skip,
                take);
            return Task.FromResult(page);
        }
    }

    public Task<ChannelUserEntry?> FindUserAsync(string channelId, string externalId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _users.TryGetValue(ChannelKeys.User(channelId, externalId), out var user);
            return Task.FromResult(user);
        }
    }

    public async Task SetOptOutAsync(string channelId, string externalId, bool optedOut, CancellationToken ct = default)
    {
        ChannelUserEntry? updated;
        lock (_gate)
        {
            var key = ChannelKeys.User(channelId, externalId);
            if (!_users.TryGetValue(key, out var existing))
            {
                if (!optedOut)
                    return;

                var now = DateTimeOffset.UtcNow;
                updated = new ChannelUserEntry
                {
                    ChannelId = channelId,
                    ExternalId = externalId,
                    FirstSeenAt = now,
                    LastSeenAt = now,
                    OptedOut = true,
                };
                _users[key] = updated;
            }
            else
            {
                updated = existing with { OptedOut = optedOut };
                _users[key] = updated;
            }
        }

        await _store.SaveUserAsync(updated, ct).ConfigureAwait(false);
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

    private void Absorb(ChannelUserEntry user, ConversationEntry? conversation)
    {
        _users[ChannelKeys.User(user.ChannelId, user.ExternalId)] = user;
        if (conversation is not null)
            _conversations[ChannelKeys.Conversation(conversation.Conversation)] = conversation;
    }

    private ChannelUserEntry UpsertUser(ChannelInboundMessage message, DateTimeOffset now)
    {
        var sender = message.Sender;
        var key = ChannelKeys.User(sender.ChannelId, sender.ExternalId);
        _users.TryGetValue(key, out var existing);
        var entry = new ChannelUserEntry
        {
            ChannelId = sender.ChannelId,
            ExternalId = sender.ExternalId,
            DisplayName = string.IsNullOrWhiteSpace(sender.DisplayName) ? existing?.DisplayName : sender.DisplayName,
            FirstSeenAt = existing?.FirstSeenAt ?? now,
            LastSeenAt = now,
            OptedOut = existing?.OptedOut ?? false,
        };
        _users[key] = entry;
        return entry;
    }

    private ConversationEntry UpsertConversation(ChannelInboundMessage message, DateTimeOffset now)
    {
        var info = message.Conversation;
        var key = ChannelKeys.Conversation(info);
        _conversations.TryGetValue(key, out var existing);
        var members = existing?.MemberExternalIds.ToList() ?? [];
        if (!members.Contains(message.Sender.ExternalId, StringComparer.Ordinal))
            members.Add(message.Sender.ExternalId);

        var entry = new ConversationEntry
        {
            Conversation = new ConversationInfo
            {
                ChannelId = info.ChannelId,
                Kind = info.Kind,
                ExternalId = info.ExternalId,
                Title = string.IsNullOrWhiteSpace(info.Title) ? existing?.Conversation.Title : info.Title,
            },
            FirstSeenAt = existing?.FirstSeenAt ?? now,
            LastSeenAt = now,
            MemberExternalIds = members,
        };
        _conversations[key] = entry;
        return entry;
    }

    private static void Prune(Queue<DateTimeOffset> queue, DateTimeOffset now)
    {
        var cutoff = now - Window;
        while (queue.Count > 0 && queue.Peek() <= cutoff)
            queue.Dequeue();
    }

    private static IReadOnlyList<T> Page<T>(IEnumerable<T> source, int skip, int take)
    {
        if (take <= 0)
            return [];
        return source.Skip(Math.Max(0, skip)).Take(take).ToList();
    }
}
