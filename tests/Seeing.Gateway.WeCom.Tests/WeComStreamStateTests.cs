using FluentAssertions;
using Seeing.Gateway.WeCom.Connection;
using Xunit;

namespace Seeing.Gateway.WeCom.Tests;

public class WeComStreamStateTests
{
    private const string ProcessingText = "🤔 Thinking...";

    [Fact]
    public async Task PublishAsync_ShouldStopKeepaliveBeforeRefresh()
    {
        var sender = new RecordingStreamSender();
        var options = new WeComOptions
        {
            StreamingEnabled = true,
            ProcessingRefreshSeconds = 5
        };

        await using var state = new WeComStreamState(sender, client: null, new WeComWsFrame(), options);
        await state.BeginAsync(CancellationToken.None);
        await state.PublishAsync("hello", CancellationToken.None);

        await Task.Delay(TimeSpan.FromSeconds(6));

        var helloIndex = sender.Records.FindIndex(r => r.Content == "hello");
        helloIndex.Should().BeGreaterThanOrEqualTo(0);

        sender.Records
            .Skip(helloIndex + 1)
            .Any(r => r.Content == ProcessingText)
            .Should().BeFalse("keepalive must stop once content is published");
    }

    [Fact]
    public async Task PublishAsync_WhenThrottled_ShouldStillStopKeepalive()
    {
        var sender = new RecordingStreamSender();
        var options = new WeComOptions
        {
            StreamingEnabled = true,
            DeltaThrottleMilliseconds = 5000,
            ProcessingRefreshSeconds = 5
        };

        await using var state = new WeComStreamState(sender, client: null, new WeComWsFrame(), options);
        await state.BeginAsync(CancellationToken.None);
        await state.PublishAsync("hello", CancellationToken.None);
        await state.PublishAsync("hello world", CancellationToken.None);

        await Task.Delay(TimeSpan.FromSeconds(6));

        sender.Records.Count(r => r.Content == "hello world").Should().Be(0,
            "second delta is throttled and should not be sent yet");

        sender.Records
            .Any(r => r.Content == ProcessingText && sender.Records.IndexOf(r) > sender.Records.FindIndex(x => x.Content == "hello"))
            .Should().BeFalse("throttled publish must still stop keepalive");
    }

    [Fact]
    public async Task CompleteAsync_AfterContent_ShouldEndWithContentOnSameStream()
    {
        var sender = new RecordingStreamSender();
        var options = new WeComOptions { StreamingEnabled = true };

        await using var state = new WeComStreamState(sender, client: null, new WeComWsFrame(), options);
        await state.BeginAsync(CancellationToken.None);
        await state.PublishAsync("partial", CancellationToken.None);
        await state.CompleteAsync("final answer", CancellationToken.None);

        sender.Records.Should().NotBeEmpty();
        sender.Records.Select(r => r.StreamId).Distinct().Should().ContainSingle();
        var last = sender.Records[^1];
        last.Content.Should().Be("final answer");
        last.Finish.Should().BeTrue();
    }

    [Fact]
    public async Task Keepalive_ShouldNeverFinishStreamBeforeComplete()
    {
        var sender = new RecordingStreamSender();
        var options = new WeComOptions
        {
            StreamingEnabled = true,
            ProcessingRefreshSeconds = 5,
            ProcessingMaxDurationSeconds = 5
        };

        await using var state = new WeComStreamState(sender, client: null, new WeComWsFrame(), options);
        await state.BeginAsync(CancellationToken.None);

        await Task.Delay(TimeSpan.FromSeconds(6));

        sender.Records.Should().OnlyContain(r => !r.Finish);
    }

    [Fact]
    public async Task CompleteAsync_AfterKeepaliveElapsed_ShouldReuseSameStreamId()
    {
        var sender = new RecordingStreamSender();
        var options = new WeComOptions
        {
            StreamingEnabled = true,
            ProcessingRefreshSeconds = 5,
            ProcessingMaxDurationSeconds = 5
        };

        await using var state = new WeComStreamState(sender, client: null, new WeComWsFrame(), options);
        await state.BeginAsync(CancellationToken.None);

        await Task.Delay(TimeSpan.FromSeconds(6));
        await state.CompleteAsync("final answer", CancellationToken.None);

        sender.Records.Select(r => r.StreamId).Distinct().Should().ContainSingle();
        sender.Records[^1].Content.Should().Be("final answer");
        sender.Records[^1].Finish.Should().BeTrue();
    }

    [Fact]
    public async Task SendInstantAsync_ShouldSkipThinkingPlaceholder()
    {
        var sender = new RecordingStreamSender();
        var options = new WeComOptions();

        await using var state = new WeComStreamState(sender, client: null, new WeComWsFrame(), options);
        await state.SendInstantAsync("✅ 已开启新对话。", CancellationToken.None);

        sender.Records.Should().ContainSingle();
        sender.Records[0].Content.Should().Be("✅ 已开启新对话。");
        sender.Records[0].Finish.Should().BeTrue();
        sender.Records.Should().NotContain(r => r.Content == ProcessingText);
    }

    [Fact]
    public async Task BeginAsync_WithZeroRefreshConfig_ShouldUseDefaultInterval()
    {
        var sender = new RecordingStreamSender();
        var options = new WeComOptions
        {
            StreamingEnabled = true,
            ProcessingRefreshSeconds = 0
        };

        await using var state = new WeComStreamState(sender, client: null, new WeComWsFrame(), options);
        await state.BeginAsync(CancellationToken.None);

        await Task.Delay(TimeSpan.FromSeconds(6));

        sender.Records.Should().ContainSingle(r => r.Content == ProcessingText && !r.Finish);
    }

    [Fact]
    public async Task PublishAsync_WithZeroThrottleConfig_ShouldUseEffectiveDefault()
    {
        var sender = new RecordingStreamSender();
        var options = new WeComOptions
        {
            StreamingEnabled = true,
            DeltaThrottleMilliseconds = 0
        };

        await using var state = new WeComStreamState(sender, client: null, new WeComWsFrame(), options);
        await state.BeginAsync(CancellationToken.None);
        await state.PublishAsync("hello", CancellationToken.None);
        await state.PublishAsync("hello world", CancellationToken.None);

        sender.Records.Count(r => r.Content == "hello world").Should().Be(0,
            "zero config should fall back to 150ms effective throttle");
    }

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
            Connection.WeComOutboundPriority priority = Connection.WeComOutboundPriority.ContentDelta)
        {
            Records.Add(new StreamRecord(streamId, content, Finish: finish));
            return Task.CompletedTask;
        }
    }

    private readonly record struct StreamRecord(string StreamId, string Content, bool Finish);
}
