using System.Text.Json;
using FluentAssertions;
using Seeing.Gateway.Models;
using Seeing.Gateway.Protocol;
using Xunit;

namespace Seeing.Gateway.Tests.Protocol;

public class GatewayWsFrameSerializerTests
{
    [Fact]
    public void SerializeDeserialize_ChatEventFrame_ShouldRoundTrip()
    {
        var gatewayEvent = new GatewayEvent
        {
            Object = GatewayEventObject.Content,
            Status = GatewayEventStatus.InProgress,
            SessionId = "ses_1",
            LoopId = "loop_1",
            SourceType = "StreamDelta",
            Data = new GatewayEventData
            {
                Delta = true,
                Text = "hello",
                StreamKind = "content"
            }
        };

        var frame = GatewayWsFrameSerializer.Create(GatewayWsFrameType.ChatEvent, "req_1", gatewayEvent);
        var json = GatewayWsFrameSerializer.Serialize(frame);
        var parsed = GatewayWsFrameSerializer.Deserialize(json);

        parsed.Should().NotBeNull();
        parsed!.Type.Should().Be(GatewayWsFrameType.ChatEvent);
        parsed.Id.Should().Be("req_1");
        parsed.Payload.Should().NotBeNull();

        var roundTripEvent = parsed.Payload!.Value.Deserialize<GatewayEvent>(GatewayWsFrameSerializer.JsonOptions);
        roundTripEvent.Should().NotBeNull();
        roundTripEvent!.SessionId.Should().Be("ses_1");
        roundTripEvent.Data!.Text.Should().Be("hello");
    }

    [Fact]
    public void SerializeDeserialize_ExecutionCompleteFrame_ShouldRoundTrip()
    {
        var payload = new GatewayExecutionCompletePayload
        {
            SessionId = "ses_1",
            ExecutionId = "exec_1",
            LoopId = "loop_1"
        };

        var frame = GatewayWsFrameSerializer.Create(GatewayWsFrameType.ExecutionComplete, "req_1", payload);
        var parsed = GatewayWsFrameSerializer.Deserialize(GatewayWsFrameSerializer.Serialize(frame));

        parsed.Should().NotBeNull();
        parsed!.Type.Should().Be(GatewayWsFrameType.ExecutionComplete);

        var roundTrip = parsed.Payload!.Value.Deserialize<GatewayExecutionCompletePayload>(GatewayWsFrameSerializer.JsonOptions);
        roundTrip!.SessionId.Should().Be("ses_1");
        roundTrip.ExecutionId.Should().Be("exec_1");
        roundTrip.LoopId.Should().Be("loop_1");
    }

    [Fact]
    public void SerializeDeserialize_ConnectedFrame_ShouldIncludeCapabilities()
    {
        var frame = GatewayWsFrameSerializer.Create(
            GatewayWsFrameType.Connected,
            payload: new GatewayConnectedPayload());

        var parsed = GatewayWsFrameSerializer.Deserialize(GatewayWsFrameSerializer.Serialize(frame));
        var payload = parsed!.Payload!.Value.Deserialize<GatewayConnectedPayload>(GatewayWsFrameSerializer.JsonOptions);

        payload!.Capabilities.Should().Contain("submit");
        payload.Capabilities.Should().Contain("cancel");
        payload.Capabilities.Should().Contain("permission");
    }
}
