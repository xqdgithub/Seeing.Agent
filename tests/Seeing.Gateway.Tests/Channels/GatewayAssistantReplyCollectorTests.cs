using FluentAssertions;
using Seeing.Gateway.Channels;
using Seeing.Gateway.Models;
using Xunit;

namespace Seeing.Gateway.Tests.Channels;

public class GatewayAssistantReplyCollectorTests
{
    [Fact]
    public void Apply_ContentDelta_ShouldAppendText()
    {
        var collector = new GatewayAssistantReplyCollector();

        collector.Apply(CreateDelta("hello"));
        collector.Apply(CreateDelta(" world"));

        collector.Text.Should().Be("hello world");
        collector.IsTerminal.Should().BeFalse();
    }

    [Fact]
    public void Apply_CompletedMessage_ShouldFillOnlyWhenAccumulatorEmpty()
    {
        var collector = new GatewayAssistantReplyCollector();

        collector.Apply(CreateCompletedMessage("final answer"));

        collector.Text.Should().Be("final answer");
    }

    [Fact]
    public void Apply_CompletedMessage_ShouldNotOverwriteStreamedText()
    {
        var collector = new GatewayAssistantReplyCollector();
        collector.Apply(CreateDelta("streamed answer"));

        collector.Apply(CreateCompletedMessage("ignored"));

        collector.Text.Should().Be("streamed answer");
    }

    [Fact]
    public void Apply_LoopComplete_ShouldMarkTerminal()
    {
        var collector = new GatewayAssistantReplyCollector();
        collector.Apply(CreateDelta("done"));

        var disposition = collector.Apply(CreateLoopComplete());

        disposition.Should().Be(GatewayReplyDisposition.RunCompleted);
        collector.Terminal.Should().Be(GatewayRunTerminal.Completed);
    }

    private static GatewayEvent CreateDelta(string text) => new()
    {
        Object = GatewayEventObject.Content,
        Status = GatewayEventStatus.InProgress,
        SessionId = "sess",
        Data = new GatewayEventData { Delta = true, Text = text }
    };

    private static GatewayEvent CreateCompletedMessage(string text) => new()
    {
        Object = GatewayEventObject.Message,
        Status = GatewayEventStatus.Completed,
        SessionId = "sess",
        Data = new GatewayEventData { Text = text, MessageRole = "assistant" }
    };

    private static GatewayEvent CreateLoopComplete() => new()
    {
        Object = GatewayEventObject.Response,
        Status = GatewayEventStatus.Completed,
        SessionId = "sess",
        SourceType = GatewayAssistantReplyCollector.LoopCompleteSourceType
    };
}
