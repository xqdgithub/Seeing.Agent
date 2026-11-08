using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Channels;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Runtime;
using Seeing.Agent.Comms.Delivery;
using Seeing.Agent.Comms.Directories;
using Seeing.Session.Core;

namespace Seeing.Agent.Comms.IntegrationTests.Support;

internal static class CommsIntegrationFixture
{
    public static ChannelDirectoryService Directory() =>
        new(new MemoryDirectoryStore(), Options.Create(new CommsOptions { RateLimitPerHour = 0 }));

    public static (OutboundDeliveryService Delivery, MemoryChannelAdapter Adapter) Delivery(IChannelDirectory directory)
    {
        var adapter = new MemoryChannelAdapter("qq");
        var registry = new ChannelAdapterRegistry(NullLogger<ChannelAdapterRegistry>.Instance);
        registry.Register(adapter);
        var delivery = new OutboundDeliveryService(
            directory,
            registry,
            new NoopDeliveryNotifier(),
            NullLogger<OutboundDeliveryService>.Instance);
        return (delivery, adapter);
    }

    public static ChannelInboundMessage Inbound(ConversationKind kind, string externalId, string sessionId, string senderId = "u1") =>
        new()
        {
            Sender = new ChannelUserIdentity { ChannelId = "qq", ExternalId = senderId },
            Conversation = new ConversationInfo
            {
                ChannelId = "qq",
                Kind = kind,
                ExternalId = externalId,
                Title = kind == ConversationKind.Group ? "room" : null
            },
            SessionId = sessionId,
            Text = "hi"
        };

    public static ISessionManager SessionManager(SessionData session)
    {
        var mock = new Mock<ISessionManager>();
        mock.Setup(m => m.EnsureSessionAsync(
                session.Id,
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>()))
            .ReturnsAsync(session);
        return mock.Object;
    }
}

internal sealed class MemoryChannelAdapter(string channelId) : IChannelAdapter
{
    public string ChannelId { get; } = channelId;

    public ChannelCapabilities Capabilities { get; } = new() { SupportsReplyAnchor = true };

    public List<OutboundMessage> Sent { get; } = [];

    public Task<OutboundDeliveryResult> SendAsync(OutboundMessage message, CancellationToken ct = default)
    {
        Sent.Add(message);
        return Task.FromResult(OutboundDeliveryResult.Ok());
    }
}

internal sealed class RecordingRuntime : ISessionRuntime
{
    public List<InboxMessage> Messages { get; } = [];

    public Task<InboxResult> DeliverAsync(InboxMessage message, CancellationToken ct = default)
    {
        Messages.Add(message);
        return Task.FromResult(InboxResult.Ok());
    }

    public Task<bool> InterruptAsync(string sessionId, CancellationToken ct = default) => Task.FromResult(false);
    public Task<int> StopAsync(string sessionId, CancellationToken ct = default) => Task.FromResult(0);
    public Task<int> CancelAllInFlightAsync(CancellationToken ct = default) => Task.FromResult(0);
    public Task WaitForIdleAsync(string sessionId, CancellationToken ct = default) => Task.CompletedTask;
    public SessionExecutionOverview GetOverview(string sessionId) => new();
    public bool HasAnyActiveExecution() => false;
    public IReadOnlyList<IMessageEvent> GetBufferedEvents(string sessionId) => [];

    public async IAsyncEnumerable<IMessageEvent> SubscribeEvents(
        string sessionId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    public Task<int> ReconcileIncompleteTasksAsync(string sessionId, CancellationToken ct = default) => Task.FromResult(0);
}

/// <summary>
/// 有状态的目录存储桩：<see cref="ChannelDirectoryService"/> 不再持有内存副本，
/// 读一律穿透到 store，所以桩必须真的记住写入内容，否则投递准入会误判为「不在目录」。
/// </summary>
internal sealed class MemoryDirectoryStore : IChannelDirectoryStore
{
    private readonly Dictionary<string, ChannelUserEntry> _users = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ConversationEntry> _conversations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BlockedConversation> _blocked = new(StringComparer.Ordinal);

    private static string UserKey(string channelId, string externalId) => channelId + "\u001f" + externalId;

    private static string ConversationKey(string channelId, ConversationKind kind, string externalId)
        => channelId + "\u001f" + kind + "\u001f" + externalId;

    public Task SaveUserAsync(ChannelUserEntry user, CancellationToken ct = default)
    {
        _users[UserKey(user.ChannelId, user.ExternalId)] = user;
        return Task.CompletedTask;
    }

    public Task SaveConversationAsync(ConversationEntry entry, CancellationToken ct = default)
    {
        var info = entry.Conversation;
        _conversations[ConversationKey(info.ChannelId, info.Kind, info.ExternalId)] = entry;
        return Task.CompletedTask;
    }

    public Task<ChannelUserEntry?> FindUserAsync(string channelId, string externalId, CancellationToken ct = default)
        => Task.FromResult(_users.TryGetValue(UserKey(channelId, externalId), out var user) ? user : null);

    public Task<ConversationEntry?> FindConversationAsync(
        string channelId, ConversationKind kind, string externalId, CancellationToken ct = default)
        => Task.FromResult(
            _conversations.TryGetValue(ConversationKey(channelId, kind, externalId), out var entry) ? entry : null);

    public Task<IReadOnlyList<ChannelUserEntry>> ListUsersAsync(
        string channelId, int skip, int take, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ChannelUserEntry>>(
            _users.Values.Where(u => u.ChannelId == channelId)
                .OrderBy(u => u.ExternalId, StringComparer.Ordinal)
                .Skip(skip).Take(take).ToList());

    public Task<IReadOnlyList<ConversationEntry>> ListConversationsAsync(
        string channelId, int skip, int take, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ConversationEntry>>(
            _conversations.Values.Where(c => c.Conversation.ChannelId == channelId)
                .OrderBy(c => c.Conversation.ExternalId, StringComparer.Ordinal)
                .Skip(skip).Take(take).ToList());

    public Task LoadIntoAsync(Action<ChannelUserEntry, ConversationEntry?> sink, CancellationToken ct = default)
    {
        foreach (var user in _users.Values.ToList())
            sink(user, null);

        foreach (var conversation in _conversations.Values.ToList())
            sink(_users.Values.First(u => u.ChannelId == conversation.Conversation.ChannelId), conversation);

        return Task.CompletedTask;
    }

    public Task DeleteUserAsync(string channelId, string externalId, CancellationToken ct = default)
    {
        _users.Remove(UserKey(channelId, externalId));
        return Task.CompletedTask;
    }

    public Task DeleteConversationAsync(
        string channelId, ConversationKind kind, string externalId, CancellationToken ct = default)
    {
        _conversations.Remove(ConversationKey(channelId, kind, externalId));
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
        => _blocked.ContainsKey(ConversationKey(channelId, kind, externalId));

    public Task SaveBlockedConversationAsync(BlockedConversation entry, CancellationToken ct = default)
    {
        _blocked[ConversationKey(entry.ChannelId, entry.Kind, entry.ExternalId)] = entry;
        return Task.CompletedTask;
    }

    public Task DeleteBlockedConversationAsync(
        string channelId, ConversationKind kind, string externalId, CancellationToken ct = default)
    {
        _blocked.Remove(ConversationKey(channelId, kind, externalId));
        return Task.CompletedTask;
    }
}
