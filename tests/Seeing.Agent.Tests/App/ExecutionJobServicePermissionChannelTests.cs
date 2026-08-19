using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.App.Execution;
using Seeing.Agent.Compression;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Permission;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.App;

public class ExecutionJobServicePermissionChannelTests
{
    [Fact]
    public void FollowGlobal_GlobalAutoApproveTrue_ShouldUseAutoApproveInstance()
    {
        using var service = CreateService(autoApproveAll: true);
        var caller = Mock.Of<IPermissionChannel>();

        var result = service.ResolvePermissionChannel(caller, SessionAutoApprove.FollowGlobal);

        result.Should().BeSameAs(DefaultPermissionChannel.AutoApproveInstance);
    }

    [Fact]
    public void FollowGlobal_GlobalAutoApproveFalse_WithCaller_ShouldUseCaller()
    {
        using var service = CreateService(autoApproveAll: false);
        var caller = Mock.Of<IPermissionChannel>();

        var result = service.ResolvePermissionChannel(caller, SessionAutoApprove.FollowGlobal);

        result.Should().BeSameAs(caller);
    }

    [Fact]
    public void FollowGlobal_GlobalAutoApproveFalse_NoCaller_ShouldDenyAll()
    {
        using var service = CreateService(autoApproveAll: false);

        var result = service.ResolvePermissionChannel(null, SessionAutoApprove.FollowGlobal);

        result.Should().BeSameAs(DenyAllPermissionChannel.Instance);
    }

    [Fact]
    public void Enabled_ShouldUseAutoApproveInstance_RegardlessOfGlobal()
    {
        using var service = CreateService(autoApproveAll: false);
        var caller = Mock.Of<IPermissionChannel>();

        var result = service.ResolvePermissionChannel(caller, SessionAutoApprove.Enabled);

        result.Should().BeSameAs(DefaultPermissionChannel.AutoApproveInstance);
    }

    [Fact]
    public void Disabled_GlobalAutoApproveTrue_WithCaller_ShouldStillUseCaller()
    {
        using var service = CreateService(autoApproveAll: true);
        var caller = Mock.Of<IPermissionChannel>();

        var result = service.ResolvePermissionChannel(caller, SessionAutoApprove.Disabled);

        result.Should().BeSameAs(caller);
    }

    [Fact]
    public void Disabled_NoCaller_ShouldDenyAll()
    {
        using var service = CreateService(autoApproveAll: true);

        var result = service.ResolvePermissionChannel(null, SessionAutoApprove.Disabled);

        result.Should().BeSameAs(DenyAllPermissionChannel.Instance);
    }

    private static ExecutionJobService CreateService(bool autoApproveAll)
    {
        var optionsMonitor = new Mock<IOptionsMonitor<SeeingAgentOptions>>();
        optionsMonitor
            .Setup(monitor => monitor.CurrentValue)
            .Returns(new SeeingAgentOptions
            {
                Permission = new PermissionOptions { AutoApproveAll = autoApproveAll }
            });

        return new ExecutionJobService(
            Mock.Of<IServiceProvider>(),
            Mock.Of<IExecutionEventPublisher>(),
            new ExecutionOptions(),
            optionsMonitor.Object,
            NullLogger<ExecutionJobService>.Instance,
            new CompressionService(null!, Mock.Of<ISessionManager>(), new CompressionOptions()));
    }
}
