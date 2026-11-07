using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Configuration;
using Seeing.Agent.Gateway.Channels;
using Seeing.Agent.Gateway.Hosting;
using Xunit;

namespace Seeing.Gateway.Tests.Hosting;

public class GatewayWorkspaceParticipantTests
{
    [Fact]
    public async Task OnApplying_WhenGatewayRunning_ShouldStopChannelHostsAndGateway()
    {
        var server = new Mock<IGatewayServer>();
        server.SetupGet(s => s.IsRunning).Returns(true);
        var hosts = new Mock<IChannelHostController>();
        var participant = new GatewayWorkspaceParticipant(
            server.Object, hosts.Object, NullLogger<GatewayWorkspaceParticipant>.Instance);

        await participant.OnApplyingAsync(
            new WorkspaceApplyContext(@"C:\a", @"C:\b"),
            TestContext.Current.CancellationToken);

        hosts.Verify(h => h.StopAllAsync(It.IsAny<CancellationToken>()), Times.Once);
        server.Verify(s => s.StopAsync(It.IsAny<CancellationToken>()), Times.Once);
        participant.WasEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task OnApplying_WhenGatewayNotRunning_ShouldNotStopServer()
    {
        var server = new Mock<IGatewayServer>();
        server.SetupGet(s => s.IsRunning).Returns(false);
        var hosts = new Mock<IChannelHostController>();
        var participant = new GatewayWorkspaceParticipant(
            server.Object, hosts.Object, NullLogger<GatewayWorkspaceParticipant>.Instance);

        await participant.OnApplyingAsync(
            new WorkspaceApplyContext(@"C:\a", @"C:\b"),
            TestContext.Current.CancellationToken);

        server.Verify(s => s.StopAsync(It.IsAny<CancellationToken>()), Times.Never);
        participant.WasEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task OnApplied_WhenGatewayWasEnabled_ShouldStartOnNewRoot()
    {
        var server = new Mock<IGatewayServer>();
        var hosts = new Mock<IChannelHostController>();
        var participant = new GatewayWorkspaceParticipant(
            server.Object, hosts.Object, NullLogger<GatewayWorkspaceParticipant>.Instance)
        {
            WasEnabled = true,
        };

        await participant.OnAppliedAsync(
            new WorkspaceApplyContext(@"C:\a", @"C:\b"),
            TestContext.Current.CancellationToken);

        server.Verify(s => s.StartAsync(It.IsAny<CancellationToken>()), Times.Once);
        hosts.Verify(h => h.StartAllAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OnApplied_WhenGatewayWasDisabled_ShouldNotStart()
    {
        var server = new Mock<IGatewayServer>();
        var hosts = new Mock<IChannelHostController>();
        var participant = new GatewayWorkspaceParticipant(
            server.Object, hosts.Object, NullLogger<GatewayWorkspaceParticipant>.Instance)
        {
            WasEnabled = false,
        };

        await participant.OnAppliedAsync(
            new WorkspaceApplyContext(@"C:\a", @"C:\b"),
            TestContext.Current.CancellationToken);

        server.Verify(s => s.StartAsync(It.IsAny<CancellationToken>()), Times.Never);
        hosts.Verify(h => h.StartAllAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
