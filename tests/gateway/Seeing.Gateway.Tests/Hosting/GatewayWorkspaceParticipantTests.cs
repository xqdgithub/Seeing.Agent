using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Configuration;
using Seeing.Agent.Gateway.Channels;
using Seeing.Agent.Gateway.Configuration;
using Seeing.Agent.Gateway.Hosting;
using Xunit;

namespace Seeing.Gateway.Tests.Hosting;

public class GatewayWorkspaceParticipantTests
{
    private static readonly WorkspaceApplyContext s_context = new(@"C:\a", @"C:\b");

    [Fact]
    public async Task OnApplying_WhenGatewayRunning_ShouldStopChannelHostsAndGateway()
    {
        var server = new Mock<IGatewayServer>();
        server.SetupGet(s => s.IsRunning).Returns(true);
        var hosts = new Mock<IChannelHostController>();
        var participant = Create(server.Object, hosts.Object, enabled: true, autoStart: true);

        await participant.OnApplyingAsync(s_context, TestContext.Current.CancellationToken);

        hosts.Verify(h => h.StopAllAsync(It.IsAny<CancellationToken>()), Times.Once);
        server.Verify(s => s.StopAsync(It.IsAny<CancellationToken>()), Times.Once);
        participant.WasRunning.Should().BeTrue();
    }

    [Fact]
    public async Task OnApplying_WhenGatewayNotRunning_ShouldNotStopServer()
    {
        var server = new Mock<IGatewayServer>();
        server.SetupGet(s => s.IsRunning).Returns(false);
        var hosts = new Mock<IChannelHostController>();
        var participant = Create(server.Object, hosts.Object, enabled: true, autoStart: true);

        await participant.OnApplyingAsync(s_context, TestContext.Current.CancellationToken);

        server.Verify(s => s.StopAsync(It.IsAny<CancellationToken>()), Times.Never);
        participant.WasRunning.Should().BeFalse();
    }

    [Fact]
    public async Task OnApplied_WhenWasRunningAndNewWorkspaceEnabled_ShouldStartOnNewRoot()
    {
        var server = new Mock<IGatewayServer>();
        var hosts = new Mock<IChannelHostController>();
        var participant = Create(server.Object, hosts.Object, enabled: true, autoStart: false);
        participant.WasRunning = true;

        await participant.OnAppliedAsync(s_context, TestContext.Current.CancellationToken);

        // AutoStart=false 但切换前就在运行：用户是手动开的，切换后继续运行。
        server.Verify(s => s.StartAsync(It.IsAny<CancellationToken>()), Times.Once);
        hosts.Verify(h => h.StartAllAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// 缺陷复现：未选工作区启动 WebUI 时 <c>Gateway</c> 节（ProjectOnly）读不到 → 从未运行 →
    /// 用户选定工作区后，即使新工作区写着 Enabled + AutoStart，也必须启动。
    /// 旧实现只看「切换前是否运行」，于是这条最常见路径一直不启动。
    /// </summary>
    [Fact]
    public async Task OnApplied_WhenNotRunningButNewWorkspaceAutoStart_ShouldStart()
    {
        var server = new Mock<IGatewayServer>();
        var hosts = new Mock<IChannelHostController>();
        var participant = Create(server.Object, hosts.Object, enabled: true, autoStart: true);
        participant.WasRunning = false;

        await participant.OnAppliedAsync(s_context, TestContext.Current.CancellationToken);

        server.Verify(s => s.StartAsync(It.IsAny<CancellationToken>()), Times.Once);
        hosts.Verify(h => h.StartAllAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OnApplied_WhenNotRunningAndNewWorkspaceAutoStartOff_ShouldNotStart()
    {
        var server = new Mock<IGatewayServer>();
        var hosts = new Mock<IChannelHostController>();
        var participant = Create(server.Object, hosts.Object, enabled: true, autoStart: false);
        participant.WasRunning = false;

        await participant.OnAppliedAsync(s_context, TestContext.Current.CancellationToken);

        server.Verify(s => s.StartAsync(It.IsAny<CancellationToken>()), Times.Never);
        hosts.Verify(h => h.StartAllAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task OnApplied_WhenNewWorkspaceDisabled_ShouldNotStartEvenIfWasRunning()
    {
        var server = new Mock<IGatewayServer>();
        var hosts = new Mock<IChannelHostController>();
        var participant = Create(server.Object, hosts.Object, enabled: false, autoStart: true);
        participant.WasRunning = true;

        await participant.OnAppliedAsync(s_context, TestContext.Current.CancellationToken);

        server.Verify(s => s.StartAsync(It.IsAny<CancellationToken>()), Times.Never);
        hosts.Verify(h => h.StartAllAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>能力集禁用 gateway 模块时，即便 Enabled + AutoStart 也不得监听端口。</summary>
    [Fact]
    public async Task OnApplied_WhenModuleDisabled_ShouldNotStart()
    {
        var server = new Mock<IGatewayServer>();
        var hosts = new Mock<IChannelHostController>();
        var participant = Create(server.Object, hosts.Object, enabled: true, autoStart: true, moduleEnabled: false);
        participant.WasRunning = false;

        await participant.OnAppliedAsync(s_context, TestContext.Current.CancellationToken);

        server.Verify(s => s.StartAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    private static GatewayWorkspaceParticipant Create(
        IGatewayServer server,
        IChannelHostController hosts,
        bool enabled,
        bool autoStart,
        bool moduleEnabled = true)
    {
        var catalog = new Mock<IModuleCatalog>();
        catalog.Setup(c => c.IsEnabled("gateway")).Returns(moduleEnabled);

        var policy = new GatewayAutoStartPolicy(
            Options.Create(new GatewayOptions { Enabled = enabled, AutoStart = autoStart }),
            catalog.Object);

        return new GatewayWorkspaceParticipant(
            server, hosts, policy, NullLogger<GatewayWorkspaceParticipant>.Instance);
    }
}
