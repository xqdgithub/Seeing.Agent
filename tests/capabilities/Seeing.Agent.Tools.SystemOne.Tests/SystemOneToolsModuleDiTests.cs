using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Extensions;
using Seeing.Agent.Core.Tools.SystemOne;
using Seeing.Agent.Core.Tools.SystemOne.Extensions;
using Xunit;

namespace Seeing.Agent.Tools.SystemOne.Tests;

public class SystemOneToolsModuleDiTests
{
    [Fact]
    public void AddSystemOneTools_应登记模块()
    {
        var services = new ServiceCollection();
        var registry = new ConfigSectionRegistry();
        services.AddSingleton<IConfigSectionRegistry>(registry);

        services.AddSystemOneTools(registry);

        services
            .Any(d => d.ServiceType == typeof(ISeeingModule)
                      && d.ImplementationInstance is SystemOneToolsModule)
            .Should().BeTrue();
    }

    [Fact]
    public async Task Activate_注册工具_Deactivate_注销()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var registry = new ConfigSectionRegistry();
        services.AddSingleton<IConfigSectionRegistry>(registry);

        services.AddSystemOneTools(registry);
        services.AddSeeingCore(registry);

        var module = (SystemOneToolsModule)services
            .Single(d => d.ServiceType == typeof(ISeeingModule)
                         && d.ImplementationInstance is SystemOneToolsModule)
            .ImplementationInstance!;

        await using var provider = services.BuildServiceProvider();
        var toolManager = provider.GetRequiredService<IToolManager>();

        await module.ActivateAsync(provider);
        toolManager.GetTool("systemone_ask").Should().NotBeNull();

        await module.DeactivateAsync(provider);
        toolManager.GetTool("systemone_ask").Should().BeNull();
    }
}
