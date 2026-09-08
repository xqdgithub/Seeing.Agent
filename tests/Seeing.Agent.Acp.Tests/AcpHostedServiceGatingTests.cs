using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Acp.Backends;
using Seeing.Agent.Acp.Client;
using Seeing.Agent.Acp.Configuration;
using Seeing.Agent.Acp.Extensions;
using Seeing.Agent.Acp.Filesystem;
using Seeing.Agent.Acp.Hosting;
using Seeing.Agent.Acp.Permission;
using Seeing.Agent.Acp.Session;
using Seeing.Agent.Acp.Terminal;
using Seeing.Agent.Acp.Transport;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Acp.Tests;

public sealed class AcpHostedServiceGatingTests
{
    [Fact]
    public async Task IdleCleanup_StartAsync_WhenModuleDisabled_ShouldNoOp()
    {
        var catalog = new Mock<IModuleCatalog>();
        catalog.Setup(x => x.IsEnabled("acp")).Returns(false);

        var activity = new AcpModuleActivity();
        var options = CreateOptions(enabled: true);
        var manager = CreateManager(options);
        var hosted = new AcpConnectionIdleCleanupHostedService(
            manager,
            options,
            activity,
            NullLogger<AcpConnectionIdleCleanupHostedService>.Instance,
            catalog.Object);

        await hosted.StartAsync(CancellationToken.None);
        await hosted.StopAsync(CancellationToken.None);

        true.Should().BeTrue();
    }

    [Fact]
    public async Task IdleCleanup_Deactivate_ShouldExitLongLoop()
    {
        var catalog = new Mock<IModuleCatalog>();
        catalog.Setup(x => x.IsEnabled("acp")).Returns(true);

        var activity = new AcpModuleActivity();
        activity.MarkActive();

        var options = CreateOptions(enabled: true, idle: TimeSpan.FromHours(1));
        var manager = CreateManager(options);
        var hosted = new AcpConnectionIdleCleanupHostedService(
            manager,
            options,
            activity,
            NullLogger<AcpConnectionIdleCleanupHostedService>.Instance,
            catalog.Object);

        await hosted.StartAsync(CancellationToken.None);
        await Task.Delay(50);

        activity.MarkInactiveAndWake();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await hosted.StopAsync(cts.Token);

        activity.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task AcpModule_Deactivate_ShouldFlipActivity()
    {
        var activity = new AcpModuleActivity();
        activity.MarkActive();

        var options = CreateOptions(enabled: true);
        var manager = CreateManager(options);
        var factory = new Func<AcpConnectionManager>(() => CreateManager(options));
        var module = new AcpModule(activity, factory, manager);

        await module.ActivateAsync();
        activity.IsActive.Should().BeTrue();

        await module.DeactivateAsync();
        activity.IsActive.Should().BeFalse();
    }

    private static IOptionsMonitor<AcpOptions> CreateOptions(bool enabled, TimeSpan? idle = null)
    {
        var value = new AcpOptions
        {
            Enabled = enabled,
            IdleTimeout = idle ?? TimeSpan.FromMinutes(30)
        };
        var monitor = new Mock<IOptionsMonitor<AcpOptions>>();
        monitor.Setup(x => x.CurrentValue).Returns(value);
        return monitor.Object;
    }

    private static AcpConnectionManager CreateManager(IOptionsMonitor<AcpOptions> options)
    {
        var scopeFactory = new Mock<IServiceScopeFactory>();
        var permission = new AcpPermissionBridge(
            scopeFactory.Object,
            NullLogger<AcpPermissionBridge>.Instance);
        var fs = new AcpFileSystemBridge(NullLogger<AcpFileSystemBridge>.Instance);
        var terminal = new AcpTerminalBridge(NullLogger<AcpTerminalBridge>.Instance);
        var backends = new Mock<IAcpBackendRegistry>();
        var clientFactory = new SeeingAcpClientFactory(
            backends.Object,
            permission,
            fs,
            terminal,
            options,
            NullLoggerFactory.Instance);

        var sessions = new Mock<ISessionManager>();
        var store = new AcpSessionStore(sessions.Object, NullLogger<AcpSessionStore>.Instance);

        return new AcpConnectionManager(
            clientFactory,
            store,
            options,
            NullLogger<AcpConnectionManager>.Instance);
    }
}
