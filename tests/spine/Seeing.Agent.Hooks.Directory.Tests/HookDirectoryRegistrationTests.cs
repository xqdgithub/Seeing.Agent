using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Extensions;
using Xunit;

namespace Seeing.Agent.Hooks.Directory.Tests;

public class HookDirectoryRegistrationTests
{
    [Fact]
    public void AddSeeingCore_FirstHostedService_ShouldBeHookDirectoryService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSeeingCore(new ConfigSectionRegistry());

        var hosted = services.Where(d => d.ServiceType == typeof(IHostedService)).ToList();
        hosted.Should().NotBeEmpty();
        hosted[0].ImplementationType.Should().Be(typeof(HookDirectoryService));
        services.Should().Contain(d =>
            d.ServiceType == typeof(IHostedService)
            && d.ImplementationType == typeof(HookDirectoryService));
    }
}
