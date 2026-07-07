using FluentAssertions;
using Xunit;

namespace Seeing.Gateway.WeCom.Tests;

public class WeComStreamRefreshTests
{
    [Fact]
    public void TryParseStreamRefresh_ShouldExtractStreamId()
    {
        var context = new WeComIncomingContext
        {
            Frame = new WeComWsFrame
            {
                Headers = new WeComWsHeaders { ReqId = "refresh_req_1" }
            },
            Message = new WeComIncomingMessage
            {
                MsgId = "refresh_msg_1",
                MsgType = "stream",
                Stream = new WeComStreamPayload { Id = "stream_abc123" }
            }
        };

        var ok = WeComMessageParser.TryParseStreamRefresh(context, out var streamId);

        ok.Should().BeTrue();
        streamId.Should().Be("stream_abc123");
    }

    [Fact]
    public async Task HandleRefreshAsync_ShouldReplyUsingRefreshFrameReqId()
    {
        var sender = new RecordingStreamSender();
        var registry = new WeComActiveStreamRegistry();
        var options = new WeComOptions { StreamingEnabled = true };

        var requestFrame = new WeComWsFrame
        {
            Headers = new WeComWsHeaders { ReqId = "original_req" }
        };

        await using var state = new WeComStreamState(sender, client: null, requestFrame, options, registry);
        await state.BeginAsync(CancellationToken.None);

        var refreshFrame = new WeComWsFrame
        {
            Headers = new WeComWsHeaders { ReqId = "refresh_req" }
        };

        await state.HandleRefreshAsync(refreshFrame, CancellationToken.None);

        sender.Records.Should().HaveCountGreaterThan(1);
        sender.Records[^1].ReqId.Should().Be("refresh_req");
        sender.Records[^1].Finish.Should().BeFalse();
    }

    [Fact]
    public void ActiveStreamRegistry_ShouldRouteRefreshToRegisteredStream()
    {
        var registry = new WeComActiveStreamRegistry();
        var handle = new FakeStreamHandle();
        registry.Register("stream_test", handle);

        var frame = new WeComWsFrame();
        var handled = registry.TryHandleRefresh(frame, "stream_test", CancellationToken.None);

        handled.Should().BeTrue();
        handle.RefreshCount.Should().Be(1);
    }

    private sealed class FakeStreamHandle : IWeComActiveStreamHandle
    {
        public int RefreshCount { get; private set; }

        public Task HandleRefreshAsync(WeComWsFrame refreshFrame, CancellationToken cancellationToken)
        {
            RefreshCount++;
            return Task.CompletedTask;
        }

        public Task NotifyConnectionDegradedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task AbortAsync(string reason, CancellationToken cancellationToken) => Task.CompletedTask;
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
            Records.Add(new StreamRecord(requestFrame.Headers?.ReqId, streamId, "🤔 Thinking...", Finish: false));
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
            Records.Add(new StreamRecord(requestFrame.Headers?.ReqId, streamId, content, Finish: finish));
            return Task.CompletedTask;
        }
    }

    private readonly record struct StreamRecord(string? ReqId, string StreamId, string Content, bool Finish);
}
