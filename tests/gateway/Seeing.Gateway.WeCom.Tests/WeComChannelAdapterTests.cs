using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Channels;
using Seeing.Gateway.Models;
using Seeing.Gateway.WeCom.Connection;
using Xunit;

namespace Seeing.Gateway.WeCom.Tests;

public class WeComChannelAdapterTests
{
    [Fact]
    public void Capabilities_ShouldDeclareNoAttachmentsAndNoReplyAnchor()
    {
        var adapter = CreateAdapter();

        // 一期不声明附件能力（null = 不支持），入站帧无失效判定故不声明回复锚点。
        adapter.Capabilities.Attachments.Should().BeNull();
        adapter.Capabilities.AttachmentsByConversationKind.Should().BeNull();
        adapter.Capabilities.SupportsReplyAnchor.Should().BeFalse();
    }

    [Theory]
    [InlineData("single", ConversationKind.Direct, "user_001", "user_001")]
    [InlineData("group", ConversationKind.Group, "member_1", "group_1")]
    public void TryMapTextEvent_ShouldFillChannelInboundMessage(
        string chatType,
        ConversationKind kind,
        string sender,
        string externalId)
    {
        var context = TextContext(chatType, sender, externalId, "hello", "msg1");

        var ok = WeComChannelAdapter.TryMapTextEvent(context, new WeComOptions(), out var inbound);

        ok.Should().BeTrue();
        inbound!.Sender.ChannelId.Should().Be("wecom");
        inbound.Sender.ExternalId.Should().Be(sender);
        inbound.Conversation.ChannelId.Should().Be("wecom");
        inbound.Conversation.Kind.Should().Be(kind);
        inbound.Conversation.ExternalId.Should().Be(externalId);
        inbound.ReplyToMsgId.Should().Be("msg1");
        inbound.Text.Should().Be("hello");
        inbound.SessionId.Should().NotBeNullOrWhiteSpace();
        inbound.CommandToken.Should().BeNull();
    }

    [Fact]
    public void TryMapTextEvent_Subscribe_ShouldSetCommandToken()
    {
        var context = TextContext("single", "user_001", "user_001", "/subscribe", "msg_sub");

        WeComChannelAdapter.TryMapTextEvent(context, new WeComOptions(), out var inbound).Should().BeTrue();

        inbound!.CommandToken.Should().Be("subscribe");
    }

    [Theory]
    [InlineData(ConversationKind.Direct, "single", "u1", "")]
    [InlineData(ConversationKind.Group, "group", "member1", "g1")]
    public void Translate_Kind_ShouldMapOutboundParameters(
        ConversationKind kind,
        string chatType,
        string userId,
        string chatId)
    {
        var parameters = WeComChannelAdapter.Translate(Target(kind, replyTo: "mid", recipient: "member1"));

        parameters.ChatType.Should().Be(chatType);
        parameters.UserId.Should().Be(userId);
        parameters.ChatId.Should().Be(chatId);
        parameters.MsgId.Should().Be("mid");

        var body = WeComChannelAdapter.ToOutboundBody(Target(kind, replyTo: "mid", recipient: "member1"), "hi");
        body.MsgType.Should().Be("text");
        body.ChatType.Should().Be(chatType);
        body.MsgId.Should().Be("mid");
        body.Text!.Content.Should().Be("hi");
        if (kind == ConversationKind.Group)
        {
            body.ChatId.Should().Be("g1");
            body.UserId.Should().Be("member1");
        }
        else
        {
            body.UserId.Should().Be("u1");
            body.ChatId.Should().BeNull();
        }
    }

    [Fact]
    public void Translate_WithoutReplyTo_ShouldOmitMsgId()
    {
        var parameters = WeComChannelAdapter.Translate(Target(ConversationKind.Direct, replyTo: null));

        parameters.MsgId.Should().BeNull();
        var body = WeComChannelAdapter.ToOutboundBody(Target(ConversationKind.Direct, replyTo: null), "push");
        body.MsgId.Should().BeNull();
        body.ChatType.Should().Be("single");
        body.UserId.Should().Be("u1");
    }

    [Fact]
    public void ToGatewayRequest_ShouldIncludeSenderConversationAndReplyTo()
    {
        var parsed = new ParsedWeComMessage
        {
            Frame = new WeComWsFrame(),
            UserId = "user_001",
            ChatId = "group_1",
            ChatType = "group",
            MessageId = "msg_1",
            InputParts = [new GatewayTextContentPart("hello")]
        };

        var request = WeComMessageParser.ToGatewayRequest(parsed, "wecom_group_group_1", "build", "plan", "m1");

        request.Sender!.ChannelId.Should().Be("wecom");
        request.Sender.ExternalId.Should().Be("user_001");
        request.Conversation!.Kind.Should().Be(ConversationKind.Group);
        request.Conversation.ChannelId.Should().Be("wecom");
        request.Conversation.ExternalId.Should().Be("group_1");
        request.ReplyToMsgId.Should().Be("msg_1");
        request.SessionId.Should().Be("wecom_group_group_1");
    }

    private static WeComChannelAdapter CreateAdapter()
    {
        var governor = new WeComOutboundGovernor();
        var outbound = new WeComOutboundChannel(governor, NullLogger<WeComOutboundChannel>.Instance);
        var session = new WeComAibotSession(NullLogger<WeComAibotSession>.Instance);
        var connection = new WeComConnectionManager(session, outbound, NullLogger<WeComConnectionManager>.Instance);
        var client = new WeComAibotWsClient(connection);
        return new WeComChannelAdapter(outbound, client, NullLogger<WeComChannelAdapter>.Instance);
    }

    private static DeliveryTarget Target(ConversationKind kind, string? replyTo, string? recipient = "member1")
    {
        var external = kind == ConversationKind.Group ? "g1" : "u1";
        return new DeliveryTarget
        {
            Conversation = new ConversationInfo
            {
                ChannelId = "wecom",
                Kind = kind,
                ExternalId = external
            },
            RecipientUserId = recipient,
            ReplyToMsgId = replyTo
        };
    }

    private static WeComIncomingContext TextContext(
        string chatType,
        string userId,
        string chatId,
        string text,
        string msgId) =>
        new()
        {
            Frame = new WeComWsFrame { Headers = new WeComWsHeaders { ReqId = "req_1" } },
            Message = new WeComIncomingMessage
            {
                MsgId = msgId,
                MsgType = "text",
                From = new WeComIncomingFrom { UserId = userId },
                ChatId = chatId,
                ChatType = chatType,
                Text = new WeComTextPayload { Content = text }
            }
        };
}
