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
}
