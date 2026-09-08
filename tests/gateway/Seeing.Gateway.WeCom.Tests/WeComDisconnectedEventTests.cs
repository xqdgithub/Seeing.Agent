using FluentAssertions;
using Xunit;

namespace Seeing.Gateway.WeCom.Tests;

public class WeComDisconnectedEventTests
{
    [Fact]
    public void TryParseDisconnectedEvent_ShouldRecognizeEventType()
    {
        var frame = new WeComWsFrame
        {
            Cmd = WeComWsCommands.EventCallback,
            Body = System.Text.Json.JsonSerializer.SerializeToElement(
                new
                {
                    msgid = "msg_1",
                    aibotid = "bot_1",
                    msgtype = "event",
                    @event = new { eventtype = "disconnected_event" }
                },
                WeComWsJson.Options)
        };

        var ok = WeComEventParser.TryParseDisconnectedEvent(frame, out var parsed);

        ok.Should().BeTrue();
        parsed.Should().NotBeNull();
        parsed!.MessageId.Should().Be("msg_1");
        parsed.AiBotId.Should().Be("bot_1");
    }

    [Fact]
    public void TryParseDisconnectedEvent_ShouldRejectEnterChat()
    {
        var frame = new WeComWsFrame
        {
            Cmd = WeComWsCommands.EventCallback,
            Body = System.Text.Json.JsonSerializer.SerializeToElement(
                new
                {
                    msgid = "msg_2",
                    msgtype = "event",
                    @event = new { eventtype = "enter_chat" }
                },
                WeComWsJson.Options)
        };

        WeComEventParser.TryParseDisconnectedEvent(frame, out _).Should().BeFalse();
    }
}
