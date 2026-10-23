using FluentAssertions;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Channels;
using Seeing.Agent.Comms.Directories;
using Xunit;

namespace Seeing.Agent.Comms.Tests.Directories;

/// <summary>入站登记同时刷新「该对话最后一条消息 ID」锚点，供被动回复使用。</summary>
public class ChannelDirectoryAnchorTests
{
    [Fact]
    public void RegisterInbound_ShouldRememberLastReplyToMsgId_PerConversation()
    {
        var directory = NewDirectory();
        directory.RegisterInbound(Inbound("qq", ConversationKind.Group, "g1", replyTo: "m1"));
        directory.RegisterInbound(Inbound("qq", ConversationKind.Group, "g1", replyTo: "m2"));

        directory.ResolveReplyAnchor(Conversation("qq", ConversationKind.Group, "g1")).Should().Be("m2");
        directory.ResolveReplyAnchor(Conversation("qq", ConversationKind.Group, "g2")).Should().BeNull();
    }

    [Fact]
    public void RegisterInbound_WithoutReplyToMsgId_ShouldKeepPreviousAnchor()
    {
        var directory = NewDirectory();
        directory.RegisterInbound(Inbound("qq", ConversationKind.Direct, "u1", replyTo: "m1"));
        directory.RegisterInbound(Inbound("qq", ConversationKind.Direct, "u1"));

        directory.ResolveReplyAnchor(Conversation("qq", ConversationKind.Direct, "u1")).Should().Be("m1");
    }

    private static ChannelDirectoryService NewDirectory()
        => new(new MemoryDirectoryStore(), Options.Create(new CommsOptions()));

    private static ChannelInboundMessage Inbound(
        string channelId,
        ConversationKind kind,
        string externalId,
        string? replyTo = null)
        => new()
        {
            Sender = new ChannelUserIdentity { ChannelId = channelId, ExternalId = "u1" },
            Conversation = Conversation(channelId, kind, externalId),
            ReplyToMsgId = replyTo,
            SessionId = "s1",
            Text = "hi",
        };

    private static ConversationInfo Conversation(
        string channelId,
        ConversationKind kind,
        string externalId)
        => new()
        {
            ChannelId = channelId,
            Kind = kind,
            ExternalId = externalId,
        };
}
