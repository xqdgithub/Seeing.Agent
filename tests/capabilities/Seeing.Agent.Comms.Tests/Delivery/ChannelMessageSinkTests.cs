using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Channels;
using Seeing.Agent.Comms.Delivery;
using Seeing.Agent.Comms.Directories;
using Xunit;

namespace Seeing.Agent.Comms.Tests.Delivery;

public class ChannelMessageSinkTests
{
    [Fact]
    public async Task SendToUserAsync_BuildsDirectTarget_AndDelivers()
    {
        var directory = new Mock<IChannelDirectory>();
        directory.Setup(d => d.FindUserAsync("qq", "u1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChannelUserEntry
            {
                ChannelId = "qq",
                ExternalId = "u1",
                DisplayName = "Ada",
                FirstSeenAt = DateTimeOffset.UtcNow,
                LastSeenAt = DateTimeOffset.UtcNow,
                OptedOut = false,
            });
        var adapter = new Mock<IChannelAdapter>();
        adapter.Setup(a => a.ChannelId).Returns("qq");
        adapter.SetupGet(a => a.Capabilities).Returns(new ChannelCapabilities { SupportsReplyAnchor = true });
        OutboundMessage? seen = null;
        adapter.Setup(a => a.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutboundMessage, CancellationToken>((m, _) => seen = m)
            .ReturnsAsync(OutboundDeliveryResult.Ok());
        var registry = new ChannelAdapterRegistry();
        registry.Register(adapter.Object);
        var delivery = new OutboundDeliveryService(directory.Object, registry, new NoopDeliveryNotifier());
        var sink = new ChannelMessageSink(directory.Object, delivery);

        var result = await sink.SendToUserAsync("qq", "u1", new OutboundContent { Text = "hi" });

        result.Delivered.Should().BeTrue();
        seen.Should().NotBeNull();
        seen!.DeliveryKind.Should().Be(OutboundDeliveryKind.Reply);
        seen!.Target.Conversation.Kind.Should().Be(ConversationKind.Direct);
        seen.Target.Conversation.ChannelId.Should().Be("qq");
        seen.Target.Conversation.ExternalId.Should().Be("u1");
        seen.Target.Conversation.Title.Should().Be("Ada");
        seen.Content.Text.Should().Be("hi");
        seen.Source.Should().Be("tool.send_message");
    }

    [Fact]
    public async Task SendToConversationAsync_PassesTargetThrough()
    {
        var directory = new ChannelDirectoryService(
            new MemoryDirectoryStore(),
            Options.Create(new CommsOptions()));
        directory.RegisterInbound(new ChannelInboundMessage
        {
            Sender = new ChannelUserIdentity { ChannelId = "qq", ExternalId = "u1" },
            Conversation = new ConversationInfo
            {
                ChannelId = "qq",
                Kind = ConversationKind.Group,
                ExternalId = "g1",
            },
            SessionId = "s",
            Text = "hi",
        });
        var adapter = new Mock<IChannelAdapter>();
        adapter.Setup(a => a.ChannelId).Returns("qq");
        adapter.SetupGet(a => a.Capabilities).Returns(new ChannelCapabilities { SupportsReplyAnchor = true });
        OutboundMessage? seen = null;
        adapter.Setup(a => a.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutboundMessage, CancellationToken>((m, _) => seen = m)
            .ReturnsAsync(OutboundDeliveryResult.Ok());
        var registry = new ChannelAdapterRegistry();
        registry.Register(adapter.Object);
        var delivery = new OutboundDeliveryService(directory, registry, new NoopDeliveryNotifier());
        var sink = new ChannelMessageSink(directory, delivery);
        var target = new DeliveryTarget
        {
            Conversation = new ConversationInfo
            {
                ChannelId = "qq",
                Kind = ConversationKind.Group,
                ExternalId = "g1",
            },
            RecipientUserId = "u1",
        };

        var result = await sink.SendToConversationAsync(target, new OutboundContent { Text = "ping" });

        result.Delivered.Should().BeTrue();
        seen!.DeliveryKind.Should().Be(OutboundDeliveryKind.Reply);
        seen.Target.Should().BeSameAs(target);
        seen.Content.Text.Should().Be("ping");
    }
}
