using FluentAssertions;
using Seeing.Agent.Abstractions.Events;
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
        collector.Apply(CreateLoopStart());

        collector.Apply(CreateDelta("hello"));
        collector.Apply(CreateDelta(" world"));

        collector.Text.Should().Be("hello world");
        collector.IsTerminal.Should().BeFalse();
    }

    [Fact]
    public void Apply_CompletedMessage_ShouldFillOnlyWhenAccumulatorEmpty()
    {
        var collector = new GatewayAssistantReplyCollector();
        collector.Apply(CreateLoopStart());

        collector.Apply(CreateCompletedMessage("final answer"));

        collector.Text.Should().Be("final answer");
    }

    [Fact]
    public void Apply_CompletedMessage_ShouldNotOverwriteStreamedText()
    {
        var collector = new GatewayAssistantReplyCollector();
        collector.Apply(CreateLoopStart());
        collector.Apply(CreateDelta("streamed answer"));

        collector.Apply(CreateCompletedMessage("ignored"));

        collector.Text.Should().Be("streamed answer");
    }

    [Fact]
    public void Apply_LoopComplete_ShouldMarkTerminal()
    {
        var collector = new GatewayAssistantReplyCollector();
        collector.Apply(CreateLoopStart());
        collector.Apply(CreateDelta("done"));

        var disposition = collector.Apply(CreateLoopComplete());

        disposition.Should().Be(GatewayReplyDisposition.RunCompleted);
        collector.Terminal.Should().Be(GatewayRunTerminal.Completed);
    }

    [Fact]
    public void Apply_EventsBeforeFirstLoopStart_ShouldBeIgnored()
    {
        // 订阅建立后、本轮 LoopStart 之前到达的事件 = 上一轮回放 / 在途尾流
        var collector = new GatewayAssistantReplyCollector();

        var staleDelta = collector.Apply(CreateDelta("stale"));
        var staleMessage = collector.Apply(CreateCompletedMessage("stale final"));
        var staleLoopComplete = collector.Apply(CreateLoopComplete());
        var staleCancelled = collector.Apply(CreateLoopCancelled());
        var staleError = collector.Apply(CreateError());

        staleDelta.Should().Be(GatewayReplyDisposition.Ignored);
        staleMessage.Should().Be(GatewayReplyDisposition.Ignored);
        staleLoopComplete.Should().Be(GatewayReplyDisposition.Ignored);
        staleCancelled.Should().Be(GatewayReplyDisposition.Ignored);
        staleError.Should().Be(GatewayReplyDisposition.Ignored);

        collector.Text.Should().BeEmpty();
        collector.Terminal.Should().Be(GatewayRunTerminal.None);
        collector.IsTerminal.Should().BeFalse();
        collector.RunStarted.Should().BeFalse();
    }

    [Fact]
    public void Apply_FreshRunAfterStaleNoise_ShouldCollectOnlyFreshReply()
    {
        // 回放尾流（含陈旧终态）不得判死本轮；真实回复以新轮 LoopStart 起算
        var collector = new GatewayAssistantReplyCollector();

        collector.Apply(CreateDelta("stale"));
        collector.Apply(CreateLoopComplete());

        collector.Apply(CreateLoopStart());
        var disposition = collector.Apply(CreateLoopComplete());

        collector.RunStarted.Should().BeTrue();
        disposition.Should().Be(GatewayReplyDisposition.RunCompleted);
        collector.Text.Should().BeEmpty();
        collector.Terminal.Should().Be(GatewayRunTerminal.Completed);
    }

    [Fact]
    public void Apply_PermissionCompleted_ShouldIgnore()
    {
        var collector = new GatewayAssistantReplyCollector();

        var disposition = collector.Apply(new GatewayEvent
        {
            Object = GatewayEventObject.Permission,
            Status = GatewayEventStatus.Completed,
            SessionId = "sess",
            Data = new GatewayEventData
            {
                PermissionId = "perm_1",
                PermissionDecision = "allow"
            }
        });

        disposition.Should().Be(GatewayReplyDisposition.Ignored);
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

    private static GatewayEvent CreateLoopStart() => new()
    {
        Object = GatewayEventObject.Response,
        Status = GatewayEventStatus.InProgress,
        SessionId = "sess",
        SourceType = MessageEventType.LoopStart
    };

    private static GatewayEvent CreateLoopCancelled() => new()
    {
        Object = GatewayEventObject.Response,
        Status = GatewayEventStatus.Cancelled,
        SessionId = "sess",
        Data = new GatewayEventData { CancelReason = "user" }
    };

    private static GatewayEvent CreateError() => new()
    {
        Object = GatewayEventObject.Error,
        Status = GatewayEventStatus.Failed,
        SessionId = "sess",
        Data = new GatewayEventData { Error = "boom" }
    };
}
