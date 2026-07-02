using System.Text;
using FluentAssertions;
using Seeing.Gateway.Models;
using Xunit;

namespace Seeing.Gateway.Client.Tests;

public class SseEventReaderTests
{
    private const string SamplePayload =
        """
        data: {"object":"content","status":"inProgress","sessionId":"ses_1","loopId":"loop_1","data":{"delta":true,"text":"hello"}}

        data: {"object":"response","status":"completed","sessionId":"ses_1","loopId":"loop_1","data":{"success":true}}

        """;

    [Fact]
    public async Task ReadEventsAsync_SamplePayload_ShouldParseGatewayEvents()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(SamplePayload));
        var events = new List<GatewayEvent>();

        await foreach (var gatewayEvent in SseEventReader.ReadEventsAsync(stream))
        {
            events.Add(gatewayEvent);
        }

        events.Should().HaveCount(2);
        events[0].Object.Should().Be(GatewayEventObject.Content);
        events[0].Status.Should().Be(GatewayEventStatus.InProgress);
        events[0].SessionId.Should().Be("ses_1");
        events[0].Data!.Text.Should().Be("hello");

        events[1].Object.Should().Be(GatewayEventObject.Response);
        events[1].Status.Should().Be(GatewayEventStatus.Completed);
        events[1].Data!.Success.Should().BeTrue();
    }

    [Fact]
    public async Task ReadEventsAsync_DoneMarker_ShouldSkipEvent()
    {
        const string payload = "data: [DONE]\n\n";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        var events = new List<GatewayEvent>();

        await foreach (var gatewayEvent in SseEventReader.ReadEventsAsync(stream))
        {
            events.Add(gatewayEvent);
        }

        events.Should().BeEmpty();
    }
}
