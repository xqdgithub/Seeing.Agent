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
    private static (SystemOneToolsModule Module, ServiceProvider Provider, IToolManager ToolManager) Build()
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

        var provider = services.BuildServiceProvider();
        return (module, provider, provider.GetRequiredService<IToolManager>());
    }

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
    public async Task Activate_注册4工具_Deactivate_全部注销()
    {
        var (module, provider, toolManager) = Build();
        await using (provider)
        {
            await module.ActivateAsync(provider);
            foreach (var id in new[] { "systemone_ask", "systemone_noul", "systemone_choice", "systemone_score" })
                toolManager.GetTool(id).Should().NotBeNull(id);

            await module.DeactivateAsync(provider);
            foreach (var id in new[] { "systemone_ask", "systemone_noul", "systemone_choice", "systemone_score" })
                toolManager.GetTool(id).Should().BeNull(id);
        }
    }
}
