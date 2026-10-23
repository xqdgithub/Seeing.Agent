using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Seeing.Gateway.Client.Tests;

public sealed class WebSocketGatewayClientTests
{
    [Fact]
    public async Task ConnectAsync_WhenGatewayIsUnavailable_ShouldProbeWithoutThrowing()
    {
        await using var client = new WebSocketGatewayClient(
            Options.Create(new GatewayClientOptions
            {
                BaseUrl = "http://127.0.0.1:1",
                WebSocketPath = "/api/gateway/ws"
            }));

        await client.ConnectAsync(
            "wecom",
            new Seeing.Agent.Abstractions.Channels.ChannelCapabilities { SupportsReplyAnchor = false },
            TestContext.Current.CancellationToken);

        client.IsConnected.Should().BeFalse();
    }
}
