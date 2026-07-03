using FluentAssertions;
using Xunit;

namespace Seeing.Gateway.WeCom.Tests;

public class WeComStreamStateTests
{
    private const string ProcessingText = "🤔 Thinking...";

    [Fact]
    public async Task UpdateContentDeltaAsync_ShouldStopKeepaliveBeforeRefresh()
    {
        var sender = new RecordingStreamSender();
        var options = new WeComOptions
        {
            StreamingEnabled = true,
            ProcessingRefreshSeconds = 5,
            ProcessingMaxDurationSeconds = 180
        };

        await using var state = new WeComStreamState(sender, new WeComWsFrame(), options);
        await state.StartProcessingIndicatorAsync(CancellationToken.None);
        await state.UpdateContentDeltaAsync("hello", CancellationToken.None);

        await Task.Delay(TimeSpan.FromSeconds(6));

        var helloIndex = sender.Records.FindIndex(r => r.Content == "hello");
        helloIndex.Should().BeGreaterThanOrEqualTo(0);

        var thinkingAfterHello = sender.Records
            .Skip(helloIndex + 1)
            .Any(r => r.Content == ProcessingText);
        thinkingAfterHello.Should().BeFalse("keepalive must stop once content phase begins");
    }

    [Fact]
    public async Task UpdateContentDeltaAsync_WhenThrottled_ShouldStillStopKeepalive()
    {
        var sender = new RecordingStreamSender();
        var options = new WeComOptions
        {
            StreamingEnabled = true,
            DeltaThrottleMilliseconds = 5000,
            ProcessingRefreshSeconds = 5,
            ProcessingMaxDurationSeconds = 180
        };

        await using var state = new WeComStreamState(sender, new WeComWsFrame(), options);
        await state.StartProcessingIndicatorAsync(CancellationToken.None);
        await state.UpdateContentDeltaAsync("hello", CancellationToken.None);
        await state.UpdateContentDeltaAsync("hello world", CancellationToken.None);

        await Task.Delay(TimeSpan.FromSeconds(6));

        sender.Records.Count(r => r.Content == "hello world").Should().Be(0,
            "second delta is throttled and should not be sent yet");

        var helloIndex = sender.Records.FindIndex(r => r.Content == "hello");
        helloIndex.Should().BeGreaterThanOrEqualTo(0);

        sender.Records
            .Skip(helloIndex + 1)
            .Any(r => r.Content == ProcessingText)
            .Should().BeFalse("throttled delta must still enter content phase and stop keepalive");
    }

    [Fact]
    public async Task FinishAsync_AfterContent_ShouldEndWithContentNotThinking()
    {
        var sender = new RecordingStreamSender();
        var options = new WeComOptions { StreamingEnabled = true };

        await using var state = new WeComStreamState(sender, new WeComWsFrame(), options);
        await state.StartProcessingIndicatorAsync(CancellationToken.None);
        await state.UpdateContentDeltaAsync("partial", CancellationToken.None);
        await state.FinishAsync("final answer", CancellationToken.None);

        sender.Records.Should().NotBeEmpty();
        var last = sender.Records[^1];
        last.Content.Should().Be("final answer");
        last.Finish.Should().BeTrue();
    }

    [Fact]
    public async Task KeepaliveTimeout_WhenNoContent_ShouldFinishWithThinking()
    {
        var sender = new RecordingStreamSender();
        var options = new WeComOptions
        {
            StreamingEnabled = true,
            ProcessingRefreshSeconds = 5,
            ProcessingMaxDurationSeconds = 5
        };

        await using var state = new WeComStreamState(sender, new WeComWsFrame(), options);
        await state.StartProcessingIndicatorAsync(CancellationToken.None);

        await Task.Delay(TimeSpan.FromSeconds(6));

        sender.Records.Should().NotBeEmpty();
        var last = sender.Records[^1];
        last.Content.Should().Be(ProcessingText);
        last.Finish.Should().BeTrue();
    }

    private sealed class RecordingStreamSender : IWeComStreamSender
    {
        public List<StreamRecord> Records { get; } = [];

        public Task SendProcessingIndicatorAsync(
            WeComWsFrame requestFrame,
            string streamId,
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
            CancellationToken cancellationToken)
        {
            Records.Add(new StreamRecord(streamId, content, Finish: finish));
            return Task.CompletedTask;
        }
    }

    private readonly record struct StreamRecord(string StreamId, string Content, bool Finish);
}
