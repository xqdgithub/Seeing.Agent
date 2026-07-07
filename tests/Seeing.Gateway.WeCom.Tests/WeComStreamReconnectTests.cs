using FluentAssertions;
using Seeing.Gateway.WeCom.Connection;
using Xunit;

namespace Seeing.Gateway.WeCom.Tests;

public class WeComStreamReconnectTests
{
    [Fact]
    public async Task PublishAsync_AfterEpochMismatch_ShouldRetryOnFlush()
    {
        var sender = new FlakyEpochSender();
        var options = new WeComOptions { StreamingEnabled = true };

        await using var state = new WeComStreamState(sender, client: null, new WeComWsFrame(), options);
        await state.BeginAsync(CancellationToken.None);
        await state.PublishAsync("hello", CancellationToken.None);

        sender.Records.Should().NotContain(r => r.Content == "hello");

        await state.FlushAsync(CancellationToken.None);

        sender.Records.Should().Contain(r => r.Content == "hello");
    }

    [Fact]
    public async Task CompleteAsync_AfterEpochMismatch_ShouldEventuallyFinish()
    {
        var sender = new FlakyEpochSender { FailRemaining = 2 };
        var options = new WeComOptions { StreamingEnabled = true };

        await using var state = new WeComStreamState(sender, client: null, new WeComWsFrame(), options);
        await state.BeginAsync(CancellationToken.None);
        await state.CompleteAsync("final answer", CancellationToken.None);

        sender.Records[^1].Content.Should().Be("final answer");
        sender.Records[^1].Finish.Should().BeTrue();
    }

    private sealed class FlakyEpochSender : IWeComStreamSender
    {
        public int FailRemaining { get; set; } = 1;

        public List<(string Content, bool Finish)> Records { get; } = [];

        public Task SendProcessingIndicatorAsync(
            WeComWsFrame requestFrame,
            string streamId,
            long connectionEpoch,
            CancellationToken cancellationToken)
        {
            Records.Add((ProcessingText, Finish: false));
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
            if (content != ProcessingText && FailRemaining > 0)
            {
                FailRemaining--;
                throw new WeComConnectionEpochException(1, 2);
            }

            Records.Add((content, finish));
            return Task.CompletedTask;
        }

        private const string ProcessingText = "🤔 Thinking...";
    }
}
