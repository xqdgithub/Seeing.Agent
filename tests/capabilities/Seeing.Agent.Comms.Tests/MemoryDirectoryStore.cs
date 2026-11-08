using Seeing.Agent.Abstractions.Channels;

namespace Seeing.Agent.Comms.Tests;

internal sealed class MemoryDirectoryStore : IChannelDirectoryStore
{
    private readonly Dictionary<string, ChannelUserEntry> _users = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ConversationEntry> _conversations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BlockedConversation> _blocked = new(StringComparer.Ordinal);

    public Task SaveUserAsync(ChannelUserEntry user, CancellationToken ct = default)
    {
        _users[user.ChannelId + "\u001f" + user.ExternalId] = user;
        return Task.CompletedTask;
    }

    public Task SaveConversationAsync(ConversationEntry entry, CancellationToken ct = default)
    {
        var info = entry.Conversation;
        _conversations[info.ChannelId + "\u001f" + info.Kind + "\u001f" + info.ExternalId] = entry;
        return Task.CompletedTask;
    }

    public Task<ChannelUserEntry?> FindUserAsync(string channelId, string externalId, CancellationToken ct = default)
    {
        _users.TryGetValue(channelId + "\u001f" + externalId, out var user);
        return Task.FromResult(user);
    }

    public Task<ConversationEntry?> FindConversationAsync(
        string channelId, ConversationKind kind, string externalId, CancellationToken ct = default)
    {
        _conversations.TryGetValue(channelId + "\u001f" + kind + "\u001f" + externalId, out var entry);
        return Task.FromResult(entry);
    }

    public Task<IReadOnlyList<ChannelUserEntry>> ListUsersAsync(
        string channelId, int skip, int take, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ChannelUserEntry>>(
            _users.Values.Where(u => u.ChannelId == channelId).Skip(skip).Take(take).ToList());

    public Task<IReadOnlyList<ConversationEntry>> ListConversationsAsync(
        string channelId, int skip, int take, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ConversationEntry>>(
            _conversations.Values.Where(c => c.Conversation.ChannelId == channelId).Skip(skip).Take(take).ToList());

    public Task LoadIntoAsync(Action<ChannelUserEntry, ConversationEntry?> sink, CancellationToken ct = default)
    {
        foreach (var user in _users.Values.ToList())
            sink(user, null);

        foreach (var conversation in _conversations.Values.ToList())
        {
            var anchor = _users.Values.FirstOrDefault(u => u.ChannelId == conversation.Conversation.ChannelId);
            if (anchor is not null)
                sink(anchor, conversation);
        }

        return Task.CompletedTask;
    }

    public Task DeleteUserAsync(string channelId, string externalId, CancellationToken ct = default)
    {
        _users.Remove(channelId + "\u001f" + externalId);
        return Task.CompletedTask;
    }

    public Task DeleteConversationAsync(
        string channelId, ConversationKind kind, string externalId, CancellationToken ct = default)
    {
        _conversations.Remove(channelId + "\u001f" + kind + "\u001f" + externalId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<BlockedConversation>> ListBlockedConversationsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<BlockedConversation>>(_blocked.Values.ToList());

    public Task<IReadOnlyList<string>> ListChannelIdsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(
            _conversations.Values.Select(c => c.Conversation.ChannelId)
                .Concat(_blocked.Values.Select(b => b.ChannelId))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(c => c, StringComparer.Ordinal)
                .ToList());

    public bool IsBlocked(string channelId, ConversationKind kind, string externalId)
        => _blocked.ContainsKey(channelId + "\u001f" + kind + "\u001f" + externalId);

    public Task SaveBlockedConversationAsync(BlockedConversation entry, CancellationToken ct = default)
    {
        _blocked[entry.ChannelId + "\u001f" + entry.Kind + "\u001f" + entry.ExternalId] = entry;
        return Task.CompletedTask;
    }

    public Task DeleteBlockedConversationAsync(
        string channelId, ConversationKind kind, string externalId, CancellationToken ct = default)
    {
        _blocked.Remove(channelId + "\u001f" + kind + "\u001f" + externalId);
        return Task.CompletedTask;
    }
}
