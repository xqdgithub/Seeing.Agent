using Microsoft.Extensions.DependencyInjection;
using FluentAssertions;
using Xunit;

namespace Seeing.Agent.App.Tests;

public class ChatOrchestratorServiceExtensionsTests
{
    [Fact]
    public void AddChatOrchestrator_ShouldRegisterSingleton()
    {
        var services = new ServiceCollection();
        services.AddChatOrchestrator();

        var descriptor = services.First(d => d.ServiceType == typeof(IChatOrchestrator));
        descriptor.Lifetime.Should().Be(ServiceLifetime.Singleton);
    }
}
