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
/// Bus 空壳可先解析；Handler 延后 Attach；不得因解析 Bus 而构造 ProviderManager。
/// </summary>
public class ReloadOrchestratorDiIntegrationTests
{
    /// <summary>
    /// 根治 A：解析发布门面不得物化 Provider 消费链（拆环）。
    /// </summary>
    [Fact]
    public void 解析Bus与编排器_不构造ProviderManager()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var registry = new ConfigSectionRegistry();
        services.AddSingleton<IConfigSectionRegistry>(registry);
        services.AddSeeingCore(registry);

        for (var i = services.Count - 1; i >= 0; i--)
        {
            var t = services[i].ServiceType;
            if (t == typeof(ProviderManager) || t == typeof(IProviderManager))
                services.RemoveAt(i);
        }

        var providerManagerConstructed = false;
        services.AddSingleton<ProviderManager>(_ =>
        {
            providerManagerConstructed = true;
            throw new InvalidOperationException("解析 Bus/编排器时不得构造 ProviderManager");
        });
        services.AddSingleton<IProviderManager>(sp => sp.GetRequiredService<ProviderManager>());

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        var orchestrator = provider.GetRequiredService<ReloadOrchestrator>();
        var bus = provider.GetRequiredService<IReloadSignalBus>();

        orchestrator.Should().NotBeNull();
        bus.Should().BeSameAs(orchestrator);
        providerManagerConstructed.Should().BeFalse();
    }

    [Fact]
    public async Task 真实组合_AttachHandlers后_推送可触发Handler()
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
            provider.AttachReloadHandlers();

            var handlers = provider.GetServices<IReloadHandler>().ToList();
            handlers.Should().Contain(h => h is ProviderReloadHandler);
            handlers.Should().Contain(h => h is ModelReloadHandler);
            handlers.Should().Contain(h => h is AgentRuntimeReloadHandler);
            handlers.Should().Contain(h => h is AgentManagerReloadHandler);
            handlers.Should().Contain(h => h is SessionReloadHandler);
            handlers.Should().Contain(h => h is ComponentManager);

            var bus = provider.GetRequiredService<IReloadSignalBus>();
            var results = await bus.PublishAsync(new WorkspaceChange
            {
                OldWorkspace = "/old",
                NewWorkspace = "/new"
            }, TestContext.Current.CancellationToken);

            results.Should().NotBeNull();
            results.Select(r => r.ComponentId).Should().Contain("session");
        }
    }
}
