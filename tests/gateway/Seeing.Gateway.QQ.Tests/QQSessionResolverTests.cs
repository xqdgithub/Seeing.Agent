using FluentAssertions;
using Seeing.Gateway.QQ;
using Xunit;

namespace Seeing.Gateway.QQ.Tests;

public class QQSessionResolverTests
{
    [Fact]
    public void Resolve_C2C_ShouldUseUserOpenId()
    {
        var msg = new ParsedQQMessage
        {
            MessageType = "c2c",
            MsgId = "1",
            SenderOpenId = "u1"
        };

        QQSessionResolver.ResolveSessionId(msg, new QQOptions()).Should().Be("qq_u1");
    }

    [Fact]
    public void Resolve_GroupShared_ShouldUseGroupOpenId()
    {
        var msg = new ParsedQQMessage
        {
            MessageType = "group",
            MsgId = "1",
            SenderOpenId = "u1",
            GroupOpenId = "g1"
        };

        QQSessionResolver.ResolveSessionId(msg, new QQOptions { ShareSessionInGroup = true })
            .Should().Be("qq_group_g1");
    }

    [Fact]
    public void Resolve_GroupNotShared_ShouldIncludeMember()
    {
        var msg = new ParsedQQMessage
        {
            MessageType = "group",
            MsgId = "1",
            SenderOpenId = "u1",
            GroupOpenId = "g1"
        };

        QQSessionResolver.ResolveSessionId(msg, new QQOptions { ShareSessionInGroup = false })
            .Should().Be("qq_group_g1_u1");
    }

    [Fact]
    public void Resolve_Guild_ShouldUseChannelId()
    {
        var msg = new ParsedQQMessage
        {
            MessageType = "guild",
            MsgId = "1",
            SenderOpenId = "u1",
            ChannelId = "c1"
        };

        QQSessionResolver.ResolveSessionId(msg, new QQOptions()).Should().Be("qq_channel_c1");
    }
}
