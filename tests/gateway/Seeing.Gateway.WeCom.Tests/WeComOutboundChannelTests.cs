using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Gateway.WeCom.Connection;
using Xunit;

namespace Seeing.Gateway.WeCom.Tests;

public class WeComOutboundChannelTests
{
    [Fact]
    public async Task SendCommandAsync_ShouldFailFastWhenEpochMismatch()
    {
        var channel = new WeComOutboundChannel(
            new WeComOutboundGovernor(),
            NullLogger<WeComOutboundChannel>.Instance);
        var transport = new WeComWebSocketTransport();
        channel.Bind(transport, epoch: 1);

        var act = () => channel.SendCommandAsync(
            WeComWsCommands.Ping,
            "req_1",
            new { },
            epoch: 2,
            CancellationToken.None);

        await act.Should().ThrowAsync<WeComConnectionEpochException>();
    }

    [Fact]
    public async Task ReplyStream_WhenNotFinish_ShouldBypassTerminalDelivery()
    {
        var channel = new WeComOutboundChannel(
            new WeComOutboundGovernor(),
            NullLogger<WeComOutboundChannel>.Instance);
        var calls = 0;
        channel.BindTerminalDelivery((_, _, _, _, _) =>
        {
            calls++;
            return Task.CompletedTask;
        });

        var act = () => channel.ReplyStreamAsync(
            new WeComWsFrame { Headers = new WeComWsHeaders { ReqId = "req_1" } },
            "stream_1",
            "delta",
            finish: false,
            epoch: 1,
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<WeComConnectionEpochException>();
        calls.Should().Be(0);
    }

    [Fact]
    public async Task ReplyStream_WhenFinish_ShouldUseTerminalDelivery()
    {
        var channel = new WeComOutboundChannel(
            new WeComOutboundGovernor(),
            NullLogger<WeComOutboundChannel>.Instance);
        string? seenStream = null;
        string? seenContent = null;
        long seenEpoch = 0;
        channel.BindTerminalDelivery((_, streamId, content, epoch, _) =>
        {
            seenStream = streamId;
            seenContent = content;
            seenEpoch = epoch;
            return Task.CompletedTask;
        });

        await channel.ReplyStreamAsync(
            new WeComWsFrame { Headers = new WeComWsHeaders { ReqId = "req_1" } },
            "stream_1",
            "done",
            finish: true,
            epoch: 3,
            TestContext.Current.CancellationToken);

        seenStream.Should().Be("stream_1");
        seenContent.Should().Be("done");
        seenEpoch.Should().Be(3);
    }
}
