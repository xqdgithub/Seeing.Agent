using FluentAssertions;
using Seeing.Gateway.WeCom.Connection;
using Xunit;

namespace Seeing.Gateway.WeCom.Tests;

public class WeComStreamPublishPipelineTests
{
    [Fact]
    public async Task SchedulePublish_ManyUpdates_ShouldCoalesceWithinThrottleWindow()
    {
        var sender = new RecordingStreamSender();
        var options = new WeComOptions
        {
            StreamingEnabled = true,
            DeltaThrottleMilliseconds = 200
        };

        await using var state = new WeComStreamState(sender, client: null, new WeComWsFrame(), options);
        await state.BeginAsync(CancellationToken.None);

        for (var i = 0; i < 10; i++)
            state.SchedulePublish($"chunk-{i}");

        await Task.Delay(TimeSpan.FromMilliseconds(350));

        var contentSends = sender.Records
            .Where(r => !r.Finish && r.Content != ProcessingText)
            .ToList();

        contentSends.Should().HaveCount(1);
        contentSends[0].Content.Should().Be("chunk-9");
    }

    [Fact]
    public async Task CompleteAsync_AfterManySchedulePublish_ShouldFinishQuicklyWithoutDrainingBacklog()
    {
        var sender = new ThrottledRecordingStreamSender();
        var options = new WeComOptions
        {
            StreamingEnabled = true,
            DeltaThrottleMilliseconds = 50
        };

        await using var state = new WeComStreamState(sender, client: null, new WeComWsFrame(), options);
        await state.BeginAsync(CancellationToken.None);

        for (var i = 0; i < 200; i++)
            state.SchedulePublish($"line-{i}");

        var completeSw = System.Diagnostics.Stopwatch.StartNew();
        await state.CompleteAsync("final answer", CancellationToken.None);
        completeSw.Stop();

        completeSw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
        sender.Records[^1].Content.Should().Be("final answer");
        sender.Records[^1].Finish.Should().BeTrue();
    }

    [Fact]
    public async Task CompleteAsync_ShouldStopBackgroundPublishLoop()
    {
        var sender = new RecordingStreamSender();
        var options = new WeComOptions
        {
            StreamingEnabled = true,
            DeltaThrottleMilliseconds = 500
        };

        await using var state = new WeComStreamState(sender, client: null, new WeComWsFrame(), options);
        await state.BeginAsync(CancellationToken.None);
        state.SchedulePublish("partial");

        await state.CompleteAsync("done", CancellationToken.None);
        state.SchedulePublish("should-not-send");

        await Task.Delay(TimeSpan.FromMilliseconds(700));

        sender.Records.Should().NotContain(r => r.Content == "should-not-send");
        sender.Records[^1].Finish.Should().BeTrue();
    }

    private const string ProcessingText = "🤔 Thinking...";

    private sealed class RecordingStreamSender : IWeComStreamSender
    {
        public List<StreamRecord> Records { get; } = [];

        public Task SendProcessingIndicatorAsync(
            WeComWsFrame requestFrame,
            string streamId,
            long connectionEpoch,
            CancellationToken cancellationToken)
        {
            Records.Add(new StreamRecord(streamId, ProcessingText, Finish: false));
            return Task.CompletedTask;
        }

        public Task ReplyStreamAsync(
            WeComWsFrame requestFrame,
            string streamId,
            string content,
            bool finish,
            long connectionEpoch,
            CancellationToken cancellationToken,
            WeComOutboundPriority priority = WeComOutboundPriority.ContentDelta)
        {
            Records.Add(new StreamRecord(streamId, content, finish));
            return Task.CompletedTask;
        }
    }

    private sealed class ThrottledRecordingStreamSender : IWeComStreamSender
    {
        private readonly WeComOutboundGovernor _governor = new();

        public List<StreamRecord> Records { get; } = [];

        public async Task SendProcessingIndicatorAsync(
            WeComWsFrame requestFrame,
            string streamId,
            long connectionEpoch,
            CancellationToken cancellationToken)
        {
            await _governor.WaitForSlotAsync(WeComOutboundPriority.Keepalive, cancellationToken);
            Records.Add(new StreamRecord(streamId, ProcessingText, Finish: false));
        }

        public async Task ReplyStreamAsync(
            WeComWsFrame requestFrame,
            string streamId,
            string content,
            bool finish,
            long connectionEpoch,
            CancellationToken cancellationToken,
            WeComOutboundPriority priority = WeComOutboundPriority.ContentDelta)
        {
            await _governor.WaitForSlotAsync(priority, cancellationToken);
            _governor.RecordSend(priority);
            Records.Add(new StreamRecord(streamId, content, finish));
        }
    }

    private readonly record struct StreamRecord(string StreamId, string Content, bool Finish);
}
