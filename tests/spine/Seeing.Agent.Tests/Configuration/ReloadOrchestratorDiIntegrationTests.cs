using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core;
using Seeing.Agent.Core.Extensions;
using Seeing.Agent.Core.Llm;
using Seeing.Agent.Llm;
using Xunit;

namespace Seeing.Agent.Tests.Configuration;

/// <summary>
/// 验证 ReloadOrchestrator 在真实 AddSeeingCore 组合中的可构造性：
/// 惰性单例需可解析、能收集全部 IReloadHandler、IReloadSignalBus 可用
/// </summary>
public class ReloadOrchestratorDiIntegrationTests
{
    [Fact]
    public async Task 真实组合_能解析编排器并收集全部Handler()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var registry = new ConfigSectionRegistry();
        services.AddSingleton<IConfigSectionRegistry>(registry);
        services.AddSeeingCore(registry);

        var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        await using (provider)
        {
            // 显式触发惰性单例构造（等价于宿主 HostedService 启动时解析）
            var orchestrator = provider.GetRequiredService<ReloadOrchestrator>();
            orchestrator.Should().NotBeNull();

            var handlers = provider.GetServices<IReloadHandler>().ToList();
            handlers.Should().Contain(h => h is ProviderReloadHandler);
            handlers.Should().Contain(h => h is ModelReloadHandler);
            handlers.Should().Contain(h => h is AgentRuntimeReloadHandler);
            handlers.Should().Contain(h => h is AgentManagerReloadHandler);
            handlers.Should().Contain(h => h is SessionReloadHandler);
            handlers.Should().Contain(h => h is ComponentManager);

            // 推送入口可用
            var bus = provider.GetRequiredService<IReloadSignalBus>();
            bus.Should().BeSameAs(orchestrator);

            var handlerRegistry = provider.GetRequiredService<IReloadHandlerRegistry>();
            handlerRegistry.Should().BeSameAs(orchestrator);
        }
    }

    [Fact]
    public async Task 真实组合_推送ConfigChange_可触发Handler()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var registry = new ConfigSectionRegistry();
        services.AddSingleton<IConfigSectionRegistry>(registry);
        services.AddSeeingCore(registry);

        var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        await using (provider)
        {
            var bus = provider.GetRequiredService<IReloadSignalBus>();

            var results = await bus.PublishAsync(new WorkspaceChange
            {
                OldWorkspace = "/old",
                NewWorkspace = "/new"
            });

            results.Should().NotBeNull();
            results.Select(r => r.ComponentId).Should().Contain("session");
        }
    }
}
