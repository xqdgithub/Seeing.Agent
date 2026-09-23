using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.SystemOne;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Extensions;
using Seeing.Agent.SystemOne.Configuration;
using Seeing.Agent.SystemOne.Extensions;
using Xunit;

namespace Seeing.Agent.SystemOne.Tests;

public class SystemOneModuleDiTests
{
    [Fact]
    public void AddSystemOne_应登记配置节与模块()
    {
        var services = new ServiceCollection();
        var registry = new ConfigSectionRegistry();
        services.AddSingleton<IConfigSectionRegistry>(registry);

        services.AddSystemOne(registry);

        registry.TryGet(SystemOneConfigStore.SectionName, out var meta).Should().BeTrue();
        meta!.FileName.Should().Be(SystemOneConfigStore.FileName);
        meta.Scope.Should().Be(ConfigScope.UserOnly);
        services
            .Any(d => d.ServiceType == typeof(ISeeingModule) && d.ImplementationInstance is SystemOneModule)
            .Should().BeTrue();
    }

    [Fact]
    public async Task Activate_注册内置Provider_Deactivate_归零()
    {
        using var env = SystemOneTestEnv.Scope(apiKey: "test-key");

        var services = new ServiceCollection();
        services.AddLogging();
        var registry = new ConfigSectionRegistry();
        services.AddSingleton<IConfigSectionRegistry>(registry);

        // 宿主约定：AddSystemOne 须在 AddSeeingCore 之前调用
        services.AddSystemOne(registry);
        services.AddSeeingCore(registry);
        // 用确定性内存存储覆盖 Core 的真实配置存储
        services.AddSingleton<IConfigSectionStore>(new TestConfigSectionStore());

        var module = (SystemOneModule)services
            .Single(d => d.ServiceType == typeof(ISeeingModule)
                         && d.ImplementationInstance is SystemOneModule)
            .ImplementationInstance!;

        await using var provider = services.BuildServiceProvider();
        var providerRegistry = provider.GetRequiredService<ISystemOneProviderRegistry>();

        await module.ActivateAsync(provider);
        providerRegistry.Providers.Should().ContainSingle(p => p.Id == "typesafe");

        await module.DeactivateAsync(provider);
        providerRegistry.Providers.Should().BeEmpty();
    }
}
