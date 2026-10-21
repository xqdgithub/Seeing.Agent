using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Core.Compression;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Execution;
using Seeing.Agent.Hosting.Execution;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.App;

/// <summary>
/// ExecutionRunner 执行级授权器构造测试：
/// 经 <see cref="IPermissionAuthorizerFactory"/> 构造，并把 SessionId + 执行级覆盖（AutoApprove）透传。
/// </summary>
public class ExecutionRunnerPermissionAuthorizerTests
{
    [Fact]
    public void ResolvePermissionAuthorizer_ShouldCreateViaFactory_WithSessionAndOverride()
    {
        var service = CreateRunner();
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
        var service = CreateRunner();
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
        var service = CreateRunner();
        var services = new Mock<IServiceProvider>();

        var result = service.ResolvePermissionAuthorizer(services.Object, "session-3", SessionAutoApprove.FollowGlobal);

        result.Should().BeNull();
    }

    private static ExecutionRunner CreateRunner()
    {
        return new ExecutionRunner(
            Mock.Of<IServiceProvider>(),
            Mock.Of<IExecutionEventPublisher>(),
            new ExecutionOptions(),
            Mock.Of<IConfigSectionStore>(),
            new CompactionRunner(
                new CompressionService(null!, Mock.Of<ISessionManager>()),
                Mock.Of<IExecutionEventPublisher>(),
                Mock.Of<ISessionManager>()),
            NullLogger<ExecutionRunner>.Instance,
            new StubSessionToolSchemaResolver());
    }
}
