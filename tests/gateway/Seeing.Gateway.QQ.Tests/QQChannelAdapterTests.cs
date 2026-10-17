using FluentAssertions;
using Seeing.Agent.Abstractions.Channels;
using Seeing.Gateway.Channels;
using Seeing.Gateway.Models;
using Seeing.Gateway.QQ.Connection;
using Seeing.Gateway.QQ.Delivery;
using System.Net;
using System.Text.Json;
using Xunit;

namespace Seeing.Gateway.QQ.Tests;

public class QQChannelAdapterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(ConversationKind.Direct, "c2c", "/v2/users/u1/messages")]
    [InlineData(ConversationKind.Group, "group", "/v2/groups/g1/messages")]
    [InlineData(ConversationKind.Guild, "guild", "/channels/c1/messages")]
    public void Translate_Kind_ShouldSelectPathAndOpenId(ConversationKind kind, string messageType, string path)
    {
        var tracker = OpenWindow("mid");
        var target = Target(kind, replyTo: "mid");

        var parsed = QQChannelAdapter.Translate(target, tracker);

        parsed.MessageType.Should().Be(messageType);
        parsed.MsgId.Should().Be("mid");
        QQHttpApiClient.ResolveSendPath(parsed).Should().Be(path);
        OpenId(parsed).Should().Be(kind == ConversationKind.Group ? "g1" : kind == ConversationKind.Guild ? "c1" : "u1");

        var body = QQHttpApiClient.BuildTextMessageBody(parsed, "hi", useMarkdown: false, keyboard: null, _ => 1);
        body.Should().ContainKey("msg_id").WhoseValue.Should().Be("mid");
    }

    [Fact]
    public void Translate_Group_ShouldKeepRecipientAsSenderOpenId()
    {
        var tracker = OpenWindow("mid");
        var parsed = QQChannelAdapter.Translate(Target(ConversationKind.Group, replyTo: "mid", recipient: "member1"), tracker);

        parsed.GroupOpenId.Should().Be("g1");
        parsed.SenderOpenId.Should().Be("member1");
    }

    [Fact]
    public void Translate_ExpiredWindow_ShouldOmitMsgId()
    {
        var tracker = new QQPassiveWindowTracker(TimeSpan.FromSeconds(300), 2, () => T0.AddSeconds(301));
        tracker.RegisterInbound("mid", T0);

        var parsed = QQChannelAdapter.Translate(Target(ConversationKind.Direct, replyTo: "mid"), tracker);

        parsed.MsgId.Should().BeEmpty();
        var body = QQHttpApiClient.BuildTextMessageBody(parsed, "push", useMarkdown: false, keyboard: null, _ => 1);
        body.Should().NotContainKey("msg_id");
    }

    [Fact]
    public void Translate_WithoutReplyTo_ShouldOmitMsgId()
    {
        var tracker = new QQPassiveWindowTracker(TimeSpan.FromSeconds(300), 2, () => T0);
        var parsed = QQChannelAdapter.Translate(Target(ConversationKind.Direct, replyTo: null), tracker);

        parsed.MsgId.Should().BeEmpty();
        QQHttpApiClient.BuildTextMessageBody(parsed, "push", false, null, _ => 1)
            .Should().NotContainKey("msg_id");
    }

    [Theory]
    [InlineData("C2C_MESSAGE_CREATE", ConversationKind.Direct, "uopenid", "uopenid")]
    [InlineData("GROUP_AT_MESSAGE_CREATE", ConversationKind.Group, "mopenid", "gopenid")]
    [InlineData("AT_MESSAGE_CREATE", ConversationKind.Guild, "user1", "ch1")]
    [InlineData("DIRECT_MESSAGE_CREATE", ConversationKind.Direct, "user1", "user1")]
    public void TryMapInbound_ShouldFillChannelInboundMessage(
        string eventType,
        ConversationKind kind,
        string sender,
        string externalId)
    {
        using var doc = JsonDocument.Parse(Payload(eventType));
        var ok = QQChannelAdapter.TryMapInbound(
            eventType,
            doc.RootElement,
            new QQOptions(),
            new QQCommandInterceptor(),
            out var inbound);

        ok.Should().BeTrue();
        inbound!.Sender.ChannelId.Should().Be("qq");
        inbound.Sender.ExternalId.Should().Be(sender);
        inbound.Conversation.Kind.Should().Be(kind);
        inbound.Conversation.ChannelId.Should().Be("qq");
        inbound.Conversation.ExternalId.Should().Be(externalId);
        inbound.ReplyToMsgId.Should().Be("msg1");
        inbound.Text.Should().Be("hello");
        inbound.SessionId.Should().NotBeNullOrWhiteSpace();
        inbound.SessionId.Should().NotContain(":");
    }

    [Fact]
    public void TryMapInbound_Subscribe_ShouldSetCommandToken()
    {
        var json = """
        { "id": "msg1", "content": "/subscribe", "author": { "user_openid": "uopenid" } }
        """;
        using var doc = JsonDocument.Parse(json);
        QQChannelAdapter.TryMapInbound(
            "C2C_MESSAGE_CREATE",
            doc.RootElement,
            new QQOptions(),
            new QQCommandInterceptor(),
            out var inbound).Should().BeTrue();

        inbound!.CommandToken.Should().Be("subscribe");
    }

    [Fact]
    public void MapApiFailure_429_ShouldBeRateLimited()
    {
        var ex = new QQApiException(HttpStatusCode.TooManyRequests, "/v2/users/u/messages", "{}");
        QQChannelAdapter.MapApiFailure(ex).Should().Be(OutboundFailureKind.RateLimited);
    }

    [Fact]
    public void MapApiFailure_Other4xx_ShouldBePlatformError()
    {
        var ex = new QQApiException(HttpStatusCode.BadRequest, "/v2/users/u/messages", "{}");
        QQChannelAdapter.MapApiFailure(ex).Should().Be(OutboundFailureKind.PlatformError);
    }

    [Fact]
    public void ReplyCollector_LoopComplete_ShouldBeRunCompleted()
    {
        var collector = new GatewayAssistantReplyCollector();
        collector.Apply(new GatewayEvent
        {
            Object = GatewayEventObject.Response,
            Status = GatewayEventStatus.InProgress,
            SessionId = "qq_direct_u1",
            SourceType = GatewayAssistantReplyCollector.LoopStartSourceType
        });
        var disposition = collector.Apply(new GatewayEvent
        {
            Object = GatewayEventObject.Response,
            Status = GatewayEventStatus.Completed,
            SessionId = "qq_direct_u1",
            SourceType = GatewayAssistantReplyCollector.LoopCompleteSourceType
        });

        disposition.Should().Be(GatewayReplyDisposition.RunCompleted);
        collector.IsTerminal.Should().BeTrue();
    }

    private static QQPassiveWindowTracker OpenWindow(string msgId)
    {
        var tracker = new QQPassiveWindowTracker(TimeSpan.FromSeconds(300), 2, () => T0);
        tracker.RegisterInbound(msgId, T0);
        return tracker;
    }

    private static DeliveryTarget Target(ConversationKind kind, string? replyTo, string? recipient = "member1")
    {
        var external = kind switch
        {
            ConversationKind.Group => "g1",
            ConversationKind.Guild => "c1",
            _ => "u1"
        };
        return new DeliveryTarget
        {
            Conversation = new ConversationInfo
            {
                ChannelId = "qq",
                Kind = kind,
                ExternalId = external
            },
            RecipientUserId = recipient,
            ReplyToMsgId = replyTo
        };
    }

    private static string? OpenId(ParsedQQMessage parsed) => parsed.MessageType switch
    {
        "group" => parsed.GroupOpenId,
        "guild" => parsed.ChannelId,
        _ => parsed.SenderOpenId
    };

    private static string Payload(string eventType) => eventType switch
    {
        "GROUP_AT_MESSAGE_CREATE" => """
            { "id": "msg1", "content": "hello", "group_openid": "gopenid", "author": { "member_openid": "mopenid" } }
            """,
        "AT_MESSAGE_CREATE" => """
            { "id": "msg1", "content": "hello", "channel_id": "ch1", "guild_id": "g1", "author": { "id": "user1" } }
            """,
        "DIRECT_MESSAGE_CREATE" => """
            { "id": "msg1", "content": "hello", "guild_id": "g1", "author": { "id": "user1" } }
            """,
        _ => """
            { "id": "msg1", "content": "hello", "author": { "user_openid": "uopenid" } }
            """
    };
}
