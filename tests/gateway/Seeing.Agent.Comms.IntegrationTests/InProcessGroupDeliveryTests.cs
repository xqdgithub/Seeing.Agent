using FluentAssertions;
using Seeing.Agent.Abstractions.Channels;
using Seeing.Agent.Comms.IntegrationTests.Support;
using Xunit;

namespace Seeing.Agent.Comms.IntegrationTests;

public class InProcessGroupDeliveryTests
{
    [Fact]
    public async Task RegisterInbound_ThenDeliverGroup_ReachesMemoryAdapter()
    {
        var directory = CommsIntegrationFixture.Directory();
        directory.RegisterInbound(CommsIntegrationFixture.Inbound(ConversationKind.Group, "g1", "qq_group_g1"));
        var (delivery, adapter) = CommsIntegrationFixture.Delivery(directory);

        var result = await delivery.DeliverAsync(new OutboundMessage
        {
            Target = new DeliveryTarget
            {
                Conversation = new ConversationInfo
                {
                    ChannelId = "qq",
                    Kind = ConversationKind.Group,
                    ExternalId = "g1",
                    Title = "room"
                }
            },
            DeliveryKind = OutboundDeliveryKind.Push,
            Content = new OutboundContent { Text = "hello group" }
        }, TestContext.Current.CancellationToken);

        result.Delivered.Should().BeTrue();
        adapter.Sent.Should().ContainSingle();
        var conversation = adapter.Sent[0].Target.Conversation;
        conversation.ChannelId.Should().Be("qq");
        conversation.Kind.Should().Be(ConversationKind.Group);
        conversation.ExternalId.Should().Be("g1");
        conversation.Title.Should().Be("room");
    }
}
