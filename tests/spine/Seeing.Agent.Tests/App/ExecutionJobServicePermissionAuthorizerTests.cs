using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Core.Compression;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Execution;
using Seeing.Agent.Hosting.Execution;
using Seeing.Agent.Configuration;
using Seeing.Session.Core;
using Seeing.Session.Management;
using Xunit;

namespace Seeing.Agent.Tests.App;

/// <summary>
/// ExecutionJobService 执行级授权器构造测试：
/// 经 <see cref="IPermissionAuthorizerFactory"/> 构造，并把 SessionId + 执行级覆盖（AutoApprove）透传。
/// </summary>
public class ExecutionJobServicePermissionAuthorizerTests
{
    [Fact]
    public void ResolvePermissionAuthorizer_ShouldCreateViaFactory_WithSessionAndOverride()
    {
        using var service = CreateService();
        var authorizer = Mock.Of<IPermissionAuthorizer>();
        var factory = new Mock<IPermissionAuthorizerFactory>();
        factory.Setup(f => f.Create("session-1", SessionAutoApprove.Enabled)).Returns(authorizer);

        var services = new Mock<IServiceProvider>();
        services.Setup(s => s.GetService(typeof(IPermissionAuthorizerFactory))).Returns(factory.Object);

        var result = service.ResolvePermissionAuthorizer(services.Object, "session-1", SessionAutoApprove.Enabled);

        result.Should().BeSameAs(authorizer);
        factory.Verify(f => f.Create("session-1", SessionAutoApprove.Enabled), Times.Once);
    }

    [Fact]
    public void ResolvePermissionAuthorizer_NullOverride_ShouldPassNull()
    {
        using var service = CreateService();
        var authorizer = Mock.Of<IPermissionAuthorizer>();
        var factory = new Mock<IPermissionAuthorizerFactory>();
        factory.Setup(f => f.Create("session-2", null)).Returns(authorizer);

        var services = new Mock<IServiceProvider>();
        services.Setup(s => s.GetService(typeof(IPermissionAuthorizerFactory))).Returns(factory.Object);

        var result = service.ResolvePermissionAuthorizer(services.Object, "session-2", null);

        result.Should().BeSameAs(authorizer);
        factory.Verify(f => f.Create("session-2", null), Times.Once);
    }

    [Fact]
    public void ResolvePermissionAuthorizer_NoFactory_ShouldReturnNull()
    {
        using var service = CreateService();
        var services = new Mock<IServiceProvider>();

        var result = service.ResolvePermissionAuthorizer(services.Object, "session-3", SessionAutoApprove.FollowGlobal);

        result.Should().BeNull();
    }

    private static ExecutionJobService CreateService()
    {
        return new ExecutionJobService(
            Mock.Of<IServiceProvider>(),
            Mock.Of<IExecutionEventPublisher>(),
            new ExecutionOptions(),
            Mock.Of<IOptionsMonitor<SeeingAgentOptions>>(),
            Mock.Of<IConfigSectionStore>(),
            NullLogger<ExecutionJobService>.Instance,
            new CompactionRunner(new CompressionService(null!, Mock.Of<ISessionManager>()), Mock.Of<IExecutionEventPublisher>(), Mock.Of<ISessionManager>()));
    }
}
