using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Seeing.Agent.Abstractions.Channels;
using Xunit;

namespace Seeing.Agent.Abstractions.Tests.Channels;

public class ChannelModelsTests
{
    private static readonly DateTimeOffset SeenAt = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Records_WithSameValues_ShouldBeEqual()
    {
        var user = new ChannelUserIdentity
        {
            ChannelId = "qq",
            ExternalId = "openid-1",
            DisplayName = "Ada"
        };
        user.Should().Be(user with { });

        var conversation = new ConversationInfo
        {
            ChannelId = "qq",
            Kind = ConversationKind.Group,
            ExternalId = "group-openid",
            Title = "room"
        };
        conversation.Should().Be(conversation with { });

        var target = new DeliveryTarget
        {
            Conversation = conversation,
            RecipientUserId = "openid-1",
            ReplyToMsgId = "msg-1"
        };
        var message = new OutboundMessage
        {
            Target = target,
            DeliveryKind = OutboundDeliveryKind.Reply,
            Text = "hello",
            Source = "tool.send_message",
            CorrelationSessionId = "sess-1",
            JobId = "job-1"
        };
        message.Should().Be(message with { });

        var userEntry = new ChannelUserEntry
        {
            ChannelId = "wecom",
            ExternalId = "userid-1",
            DisplayName = "Bob",
            FirstSeenAt = SeenAt,
            LastSeenAt = SeenAt,
            OptedOut = false
        };
        userEntry.Should().Be(userEntry with { });

        var members = new[] { "openid-1", "openid-2" };
        var conversationEntry = new ConversationEntry
        {
            Conversation = conversation,
            FirstSeenAt = SeenAt,
            LastSeenAt = SeenAt,
            MemberExternalIds = members
        };
        conversationEntry.Should().Be(conversationEntry with { MemberExternalIds = members });

        var inbound = new ChannelInboundMessage
        {
            Sender = user,
            Conversation = conversation,
            ReplyToMsgId = "msg-1",
            SessionId = "sess-1",
            Text = "hi",
            CommandToken = "new",
            AttachmentParts = null
        };
        inbound.Should().Be(inbound with { });
    }

    [Fact]
    public void Records_WithDifferentValues_ShouldNotBeEqual()
    {
        var left = new ChannelUserIdentity { ChannelId = "qq", ExternalId = "a" };
        var right = left with { ExternalId = "b" };
        left.Should().NotBe(right);
    }

    [Fact]
    public void OutboundDeliveryResult_Ok_ShouldMarkDeliveredWithoutFailure()
    {
        var result = OutboundDeliveryResult.Ok();

        result.Delivered.Should().BeTrue();
        result.FailureKind.Should().BeNull();
        result.Detail.Should().BeNull();
    }

    [Fact]
    public void OutboundDeliveryResult_Fail_ShouldCarryKindAndOptionalDetail()
    {
        var result = OutboundDeliveryResult.Fail(OutboundFailureKind.RateLimited, "slow down");

        result.Delivered.Should().BeFalse();
        result.FailureKind.Should().Be(OutboundFailureKind.RateLimited);
        result.Detail.Should().Be("slow down");

        var withoutDetail = OutboundDeliveryResult.Fail(OutboundFailureKind.OptedOut);
        withoutDetail.Delivered.Should().BeFalse();
        withoutDetail.FailureKind.Should().Be(OutboundFailureKind.OptedOut);
        withoutDetail.Detail.Should().BeNull();
    }

    [Theory]
    [InlineData(ConversationKind.Direct, "\"direct\"")]
    [InlineData(ConversationKind.Group, "\"group\"")]
    [InlineData(ConversationKind.Guild, "\"guild\"")]
    public void ConversationKind_Serialize_ShouldUseCamelCaseEnumNames(ConversationKind kind, string expectedJson)
    {
        var json = JsonSerializer.Serialize(kind, CreateGatewayCompatibleOptions());

        json.Should().Be(expectedJson);
        JsonSerializer.Deserialize<ConversationKind>(json, CreateGatewayCompatibleOptions()).Should().Be(kind);
    }

    [Fact]
    public void ConversationInfo_Serialize_ShouldUseCamelCasePropertyAndEnum()
    {
        var info = new ConversationInfo
        {
            ChannelId = "wecom",
            Kind = ConversationKind.Direct,
            ExternalId = "user-1"
        };

        var json = JsonSerializer.Serialize(info, CreateGatewayCompatibleOptions());

        json.Should().Contain("\"kind\":\"direct\"");
        json.Should().Contain("\"channelId\":\"wecom\"");
        json.Should().Contain("\"externalId\":\"user-1\"");
    }

    /// <summary>
    /// 与 <c>GatewayWsFrameSerializer.JsonOptions</c> 同构：CamelCase 属性名 + JsonStringEnumConverter(CamelCase)。
    /// </summary>
    private static JsonSerializerOptions CreateGatewayCompatibleOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
