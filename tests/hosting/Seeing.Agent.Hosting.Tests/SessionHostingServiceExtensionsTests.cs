using Microsoft.Extensions.DependencyInjection;
using FluentAssertions;
using Seeing.Agent.Abstractions.Runtime;
using Seeing.Agent.Hosting;
using Seeing.Agent.Hosting.Execution;
using Seeing.Session.Persistence;
using Xunit;

namespace Seeing.Agent.Hosting.Tests;

public class SessionHostingServiceExtensionsTests
{
    [Fact]
    public void AddExecutionEngine_ShouldRegisterSessionRuntimeAndDirectoryAsSingleton()
    {
        var services = new ServiceCollection();
        services.AddExecutionEngine();

        var runtime = services.First(d => d.ServiceType == typeof(ISessionRuntime));
        runtime.Lifetime.Should().Be(ServiceLifetime.Singleton);

        var directory = services.First(d => d.ServiceType == typeof(ISessionDirectory));
        directory.Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    [Fact]
    public void AddExecutionEngine_ShouldRegisterArchivalOptionsAndScanService()
    {
        var services = new ServiceCollection();
        services.AddExecutionEngine();

        services.Should().Contain(d => d.ServiceType == typeof(SessionArchivalOptions));
        services.Should().Contain(d =>
            d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)
            && d.ImplementationFactory != null);
    }

    [Fact]
    public void AddSessionHosting_ShouldRegisterTaskTools()
    {
        var services = new ServiceCollection();
        services.AddSessionHosting();

        services.Should().Contain(d => d.ServiceType == typeof(Seeing.Agent.Hosting.Tools.TaskTool));
        services.Should().Contain(d => d.ServiceType == typeof(Seeing.Agent.Hosting.Tools.TaskStatusTool));
    }
}
