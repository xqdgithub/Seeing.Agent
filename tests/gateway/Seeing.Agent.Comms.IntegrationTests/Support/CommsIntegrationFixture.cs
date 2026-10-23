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

internal sealed class MemoryDirectoryStore : IChannelDirectoryStore
{
    public Task SaveUserAsync(ChannelUserEntry user, CancellationToken ct = default) => Task.CompletedTask;

    public Task SaveConversationAsync(ConversationEntry entry, CancellationToken ct = default) => Task.CompletedTask;

    public Task<ChannelUserEntry?> FindUserAsync(string channelId, string externalId, CancellationToken ct = default) =>
        Task.FromResult<ChannelUserEntry?>(null);

    public Task<ConversationEntry?> FindConversationAsync(
        string channelId, ConversationKind kind, string externalId, CancellationToken ct = default) =>
        Task.FromResult<ConversationEntry?>(null);

    public Task<IReadOnlyList<ChannelUserEntry>> ListUsersAsync(
        string channelId, int skip, int take, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ChannelUserEntry>>([]);

    public Task<IReadOnlyList<ConversationEntry>> ListConversationsAsync(
        string channelId, int skip, int take, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ConversationEntry>>([]);

    public Task LoadIntoAsync(Action<ChannelUserEntry, ConversationEntry?> sink, CancellationToken ct = default) =>
        Task.CompletedTask;
}
