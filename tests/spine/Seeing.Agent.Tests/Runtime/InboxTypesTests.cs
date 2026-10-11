using FluentAssertions;
using Seeing.Agent.Abstractions.Runtime;
using Xunit;

namespace Seeing.Agent.Tests.Runtime;

public class InboxTypesTests
{
    [Fact]
    public void InboxMessage_ShouldDefault_WakeAndUser()
    {
        var msg = new InboxMessage
        {
            TargetSessionId = "s1",
            Sender = new InboxSender { Kind = InboxSenderKind.Human },
            Body = "hi"
        };

        msg.Delivery.Should().Be(InboxDelivery.Wake);
        msg.Presentation.Should().Be(InboxPresentation.User);
    }

    [Fact]
    public void InboxMetadataKeys_ShouldExposeExpectedKeys()
    {
        InboxMetadataKeys.Delivery.Should().Be("inbox.delivery");
        InboxMetadataKeys.Consumed.Should().Be("inbox.consumed");
        InboxMetadataKeys.SenderKind.Should().Be("inbox.sender_kind");
        InboxMetadataKeys.SenderId.Should().Be("inbox.sender_id");
        InboxMetadataKeys.SenderName.Should().Be("inbox.sender_name");
        InboxMetadataKeys.Presentation.Should().Be("inbox.presentation");
        InboxMetadataKeys.Source.Should().Be("inbox.source");
        InboxMetadataKeys.Kind.Should().Be("inbox.kind");
    }
}
