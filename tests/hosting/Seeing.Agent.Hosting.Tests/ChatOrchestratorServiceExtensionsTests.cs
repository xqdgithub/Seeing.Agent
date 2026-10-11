using Microsoft.Extensions.DependencyInjection;
using FluentAssertions;
using Seeing.Agent.Abstractions.Runtime;
using Seeing.Agent.Hosting;
using Xunit;

namespace Seeing.Agent.Hosting.Tests;

public class ChatOrchestratorServiceExtensionsTests
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
    public void AddChatOrchestrator_ShouldRegisterTaskTools()
    {
        var services = new ServiceCollection();
        services.AddChatOrchestrator();

        services.Should().Contain(d => d.ServiceType == typeof(Seeing.Agent.Hosting.Tools.TaskTool));
        services.Should().Contain(d => d.ServiceType == typeof(Seeing.Agent.Hosting.Tools.TaskStatusTool));
    }
}
