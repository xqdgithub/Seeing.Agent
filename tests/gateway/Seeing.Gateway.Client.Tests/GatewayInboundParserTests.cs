using FluentAssertions;
using Seeing.Gateway.Client;
using Seeing.Gateway.Protocol;
using Xunit;

namespace Seeing.Gateway.Client.Tests;

public class GatewayInboundParserTests
{
    [Fact]
    public void Parse_ChatEventFrame_ShouldExtractGatewayEvent()
    {
        var frame = GatewayWsFrameSerializer.Create(
            GatewayWsFrameType.ChatEvent,
            "req_1",
            new
            {
                @object = "content",
                status = "inProgress",
                sessionId = "ses_1",
                data = new { delta = true, text = "hi" }
            });

        var inbound = GatewayInboundParser.Parse(frame);

        inbound.Type.Should().Be(GatewayWsFrameType.ChatEvent);
        inbound.Id.Should().Be("req_1");
        inbound.Event.Should().NotBeNull();
        inbound.Event!.SessionId.Should().Be("ses_1");
        inbound.Event.Data!.Text.Should().Be("hi");
    }

    [Fact]
    public void Parse_ErrorFrame_ShouldExtractErrorPayload()
    {
        var frame = GatewayWsFrameSerializer.Create(
            GatewayWsFrameType.Error,
            payload: new GatewayWsErrorPayload { Message = "bad frame", Code = "invalid_json" });

        var inbound = GatewayInboundParser.Parse(frame);

        inbound.Error!.Message.Should().Be("bad frame");
        inbound.Error.Code.Should().Be("invalid_json");
    }

    [Fact]
    public void Parse_ChannelOutboundFrame_ShouldExtractPayload()
    {
        var frame = GatewayWsFrameSerializer.Create(
            GatewayWsFrameType.ChannelOutbound,
            "out_1",
            new GatewayChannelOutboundPayload
            {
                Channel = "qq",
                SessionId = "qq_group_ABC",
                Text = "hello",
                Source = "scheduler.cron"
            });

        var inbound = GatewayInboundParser.Parse(frame);

        inbound.Type.Should().Be(GatewayWsFrameType.ChannelOutbound);
        inbound.ChannelOutbound!.Channel.Should().Be("qq");
        inbound.ChannelOutbound.SessionId.Should().Be("qq_group_ABC");
        inbound.ChannelOutbound.Text.Should().Be("hello");
    }
}
