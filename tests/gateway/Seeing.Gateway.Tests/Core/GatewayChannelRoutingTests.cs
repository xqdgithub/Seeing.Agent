using FluentAssertions;
using Moq;
using Seeing.Agent.Abstractions.Channels;
using Seeing.Agent.Gateway.Core;
using Seeing.Gateway.Protocol;
using System.Net.WebSockets;
using Xunit;

namespace Seeing.Gateway.Tests.Core;

public class GatewayChannelRoutingTests
{
    [Fact]
    public void RegisterChannel_InjectsProxy_LastRegistrationWins_UnregisterDropsCurrentOnly()
    {
        var registry = new RecordingRegistry();
        var manager = new GatewayConnectionManager(adapterRegistry: registry);
        var firstSocket = OpenSocket();
        var secondSocket = OpenSocket();
        var first = manager.Register(firstSocket.Object);
        var second = manager.Register(secondSocket.Object);

        manager.RegisterChannel(first.ConnectionId, "qq", new ChannelRegisterCapabilities
        {
            ConversationKinds = ["group"],
            SupportsStreaming = true
        });
        manager.RegisterChannel(second.ConnectionId, "qq");

        registry.Adapters.Should().ContainSingle();
        registry.ReplacementCount.Should().Be(1);
        registry.TryGet("qq", out var adapter).Should().BeTrue();
        adapter.Should().BeOfType<RemoteChannelAdapterProxy>();
        adapter!.ChannelId.Should().Be("qq");

        var pushed = manager.PushChannelOutbound(new GatewayChannelOutboundPayload
        {
            Channel = "qq",
            CorrelationSessionId = "sess",
            Text = "hi"
        });

        pushed.Should().BeTrue();
        SendCount(firstSocket).Should().Be(0);
        SendCount(secondSocket).Should().Be(1);

        manager.Unregister(first.ConnectionId);
        registry.TryGet("qq", out _).Should().BeTrue();

        manager.Unregister(second.ConnectionId);
        registry.TryGet("qq", out _).Should().BeFalse();
        manager.PushChannelOutbound(new GatewayChannelOutboundPayload
        {
            Channel = "qq",
            CorrelationSessionId = "sess",
            Text = "again"
        }).Should().BeFalse();
        manager.GetRegisteredChannels().Should().BeEmpty();
    }

    [Fact]
    public async Task RemoteProxy_SendAsync_DeliveredMeansFrameHandedToConnection()
    {
        GatewayChannelOutboundPayload? captured = null;
        var proxy = new RemoteChannelAdapterProxy("qq", payload =>
        {
            captured = payload;
            return true;
        });

        var result = await proxy.SendAsync(new OutboundMessage
        {
            Target = new DeliveryTarget
            {
                Conversation = new ConversationInfo
                {
                    ChannelId = "qq",
                    Kind = ConversationKind.Direct,
                    ExternalId = "u1"
                },
                RecipientUserId = "u1",
                ReplyToMsgId = "m1"
            },
            DeliveryKind = OutboundDeliveryKind.SystemNotice,
            Text = "hello",
            Source = "scheduler.cron",
            CorrelationSessionId = "sess-1",
            JobId = "job-1"
        });

        result.Delivered.Should().BeTrue();
        captured.Should().NotBeNull();
        captured!.Channel.Should().Be("qq");
        captured.Conversation!.ExternalId.Should().Be("u1");
        captured.DeliveryKind.Should().Be(OutboundDeliveryKind.SystemNotice);
        captured.ReplyToMsgId.Should().Be("m1");
        captured.CorrelationSessionId.Should().Be("sess-1");
        captured.JobId.Should().Be("job-1");
        captured.UserId.Should().Be("u1");
    }

    [Fact]
    public void Notifier_PushesAssistantEventToSessionSubscribers()
    {
        var socket = OpenSocket();
        var manager = new GatewayConnectionManager();
        var connection = manager.Register(socket.Object);
        manager.SubscribeSession(connection.ConnectionId, "sess-1");
        var notifier = new GatewayDeliveryNotifier(manager);

        notifier.NotifyDeliveredAsync(
            new OutboundMessage
            {
                Target = new DeliveryTarget
                {
                    Conversation = new ConversationInfo
                    {
                        ChannelId = "qq",
                        Kind = ConversationKind.Direct,
                        ExternalId = "u1"
                    }
                },
                DeliveryKind = OutboundDeliveryKind.SystemNotice,
                Text = "hello",
                Source = "scheduler.cron",
                CorrelationSessionId = "sess-1"
            },
            OutboundDeliveryResult.Ok()).GetAwaiter().GetResult();

        SendCount(socket).Should().Be(1);
    }

    private static int SendCount(Mock<WebSocket> socket) =>
        socket.Invocations.Count(invocation => invocation.Method.Name == "SendAsync");

    private static Mock<WebSocket> OpenSocket()
    {
        var socket = new Mock<WebSocket>();
        socket.SetupGet(s => s.State).Returns(WebSocketState.Open);
        socket
            .Setup(s => s.SendAsync(
                It.IsAny<ArraySegment<byte>>(),
                It.IsAny<WebSocketMessageType>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return socket;
    }

    private sealed class RecordingRegistry : IChannelAdapterRegistry
    {
        public List<IChannelAdapter> Adapters { get; } = [];
        public int ReplacementCount { get; private set; }

        public void Register(IChannelAdapter adapter)
        {
            var existing = Adapters.FindIndex(item =>
                string.Equals(item.ChannelId, adapter.ChannelId, StringComparison.Ordinal));
            if (existing >= 0)
            {
                ReplacementCount++;
                Adapters.RemoveAt(existing);
            }

            Adapters.Add(adapter);
        }

        public bool TryGet(string channelId, out IChannelAdapter? adapter)
        {
            adapter = Adapters.LastOrDefault(item =>
                string.Equals(item.ChannelId, channelId, StringComparison.Ordinal));
            return adapter is not null;
        }

        public IReadOnlyList<string> RegisteredChannels =>
            Adapters.Select(item => item.ChannelId).Distinct(StringComparer.Ordinal).ToList();

        public void Unregister(string channelId) =>
            Adapters.RemoveAll(item => string.Equals(item.ChannelId, channelId, StringComparison.Ordinal));
    }
}
