using FluentAssertions;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Channels;
using Seeing.Gateway.QQ;
using Xunit;

namespace Seeing.Agent.Comms.IntegrationTests;

public class QQRotationDeliveryTargetTests
{
    [Fact]
    public void NewRotation_ChangesSessionId_NotDeliveryTarget()
    {
        var options = Options.Create(new QQOptions { ShareSessionInGroup = true });
        var tracker = new QQSessionTracker(options);

        var group = new ParsedQQMessage
        {
            MessageType = "group",
            MsgId = "m-group",
            GroupOpenId = "g1",
            SenderOpenId = "u1",
            Text = "/new"
        };
        var direct = new ParsedQQMessage
        {
            MessageType = "c2c",
            MsgId = "m-c2c",
            SenderOpenId = "u9",
            Text = "/new"
        };

        var groupBefore = tracker.Resolve(group);
        var groupAfter = tracker.Rotate(group);
        var directBefore = tracker.Resolve(direct);
        var directAfter = tracker.Rotate(direct);

        groupAfter.Should().NotBe(groupBefore);
        groupAfter.Should().StartWith(groupBefore + "_");
        directAfter.Should().NotBe(directBefore);

        var groupParsed = QQChannelAdapter.ToParsed(new DeliveryTarget
        {
            Conversation = QQChannelAdapter.ToConversation(group)
        });
        groupParsed.MessageType.Should().Be("group");
        groupParsed.GroupOpenId.Should().Be("g1");
        groupParsed.GroupOpenId.Should().NotBe(groupAfter);

        var directParsed = QQChannelAdapter.ToParsed(new DeliveryTarget
        {
            Conversation = QQChannelAdapter.ToConversation(direct)
        });
        directParsed.MessageType.Should().Be("c2c");
        directParsed.SenderOpenId.Should().Be("u9");
        directParsed.SenderOpenId.Should().NotBe(directAfter);
    }
}
