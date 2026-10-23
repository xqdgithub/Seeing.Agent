using System.Text.Json;
using FluentAssertions;
using Seeing.Agent.Abstractions.Channels;
using Seeing.Gateway.Models;
using Seeing.Gateway.Protocol;
using Xunit;

namespace Seeing.Gateway.Tests.Protocol;

public class PayloadV2SerializationTests
{
    [Fact]
    public void OutboundPayload_SerializesConversationDeliveryAndCorrelationSession()
    {
        var payload = new GatewayChannelOutboundPayload
        {
            Channel = "qq",
            Conversation = new ConversationInfo
            {
                ChannelId = "qq",
                Kind = ConversationKind.Group,
                ExternalId = "g-1"
            },
            DeliveryKind = OutboundDeliveryKind.Push,
            CorrelationSessionId = "sess-1",
            Content = new OutboundContent { Text = "hi" }
        };

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload, GatewayWsFrameSerializer.JsonOptions));
        var root = doc.RootElement;

        root.GetProperty("channel").GetString().Should().Be("qq");
        root.GetProperty("deliveryKind").GetString().Should().Be("push");
        root.GetProperty("correlationSessionId").GetString().Should().Be("sess-1");
        root.TryGetProperty("sessionId", out _).Should().BeFalse();

        var conversation = root.GetProperty("conversation");
        conversation.GetProperty("kind").GetString().Should().Be("group");
        conversation.GetProperty("externalId").GetString().Should().Be("g-1");
    }

    [Fact]
    public void ChannelRegisterPayload_ShouldRoundTripCapabilities()
    {
        var payload = new GatewayChannelRegisterPayload
        {
            Channel = "qq",
            Capabilities = new ChannelCapabilities
            {
                SupportsReplyAnchor = true,
                Attachments = new() { Kinds = [OutboundAttachmentKind.File], MaxBytes = 1024, MaxPerMessage = 5 }
            }
        };

        var json = JsonSerializer.Serialize(payload, GatewayWsFrameSerializer.JsonOptions);
        var back = JsonSerializer.Deserialize<GatewayChannelRegisterPayload>(json, GatewayWsFrameSerializer.JsonOptions)!;
        back.Capabilities.SupportsReplyAnchor.Should().BeTrue();
        back.Capabilities.Attachments!.MaxPerMessage.Should().Be(5);
    }

    [Fact]
    public void ChannelRegisterPayload_WithoutCapabilities_ShouldFailDeserialization()
    {
        const string json = """{"channel":"qq"}""";
        Action act = () => JsonSerializer.Deserialize<GatewayChannelRegisterPayload>(json, GatewayWsFrameSerializer.JsonOptions);
        act.Should().Throw<JsonException>();   // 服务端据此回 invalid_capabilities，不注册、不断连
    }

    [Fact]
    public void CancelPayload_SerializesMode()
    {
        var payload = new GatewayCancelPayload
        {
            SessionId = "sess-1",
            Mode = "stop"
        };

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload, GatewayWsFrameSerializer.JsonOptions));
        doc.RootElement.GetProperty("sessionId").GetString().Should().Be("sess-1");
        doc.RootElement.GetProperty("mode").GetString().Should().Be("stop");
    }

    [Fact]
    public void GatewayRequest_CarriesSenderConversationReply_WithoutUserId()
    {
        var request = new GatewayRequest
        {
            SessionId = "sess-1",
            Sender = new ChannelUserIdentity { ChannelId = "qq", ExternalId = "openid-1" },
            Conversation = new ConversationInfo
            {
                ChannelId = "qq",
                Kind = ConversationKind.Group,
                ExternalId = "g-1"
            },
            ReplyToMsgId = "msg-9"
        };

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(request, GatewayWsFrameSerializer.JsonOptions));
        var root = doc.RootElement;

        root.TryGetProperty("userId", out _).Should().BeFalse();
        root.GetProperty("sender").GetProperty("externalId").GetString().Should().Be("openid-1");
        root.GetProperty("conversation").GetProperty("kind").GetString().Should().Be("group");
        root.GetProperty("replyToMsgId").GetString().Should().Be("msg-9");
    }
}
