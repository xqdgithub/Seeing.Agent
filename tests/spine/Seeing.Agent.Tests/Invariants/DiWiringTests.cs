using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Core.Extensions;
using Seeing.Agent.Core.Llm;
using Seeing.Agent.Core.Llm.Hooks;
using Xunit;

namespace Seeing.Agent.Tests.Invariants;

/// <summary>
/// Task R-5：DI 接线唯一属主守门。
/// <para>脊柱核心必须自己把「路由 / 横切调用 / 载荷适配」三件套登记齐，宿主只调
/// <c>AddSeeingCore</c> 就能解析；同时旧的 <c>IProviderEndpointLookup</c> 端点旁路必须彻底消失
/// （spec §9.1 / §9.2：Embedding 不再有第二条取端点的路）。</para>
/// </summary>
public class DiWiringTests
{
    private static IServiceCollection CreateServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var registry = new Seeing.Agent.Core.Configuration.ConfigSectionRegistry();
        services.AddSingleton<Seeing.Agent.Abstractions.Configuration.IConfigSectionRegistry>(registry);
        services.AddSeeingCore(registry);
        return services;
    }

    [Fact]
    public void AddSeeingCore_ShouldRegisterRouterInvokerAndAdapterRegistry()
    {
        var services = CreateServices();

        services.Single(d => d.ServiceType == typeof(ILlmRouter))
            .ImplementationType.Should().Be(typeof(LlmRouter));
        services.Single(d => d.ServiceType == typeof(ILlmInvoker))
            .ImplementationType.Should().Be(typeof(LlmInvoker));
        services.Should().Contain(d => d.ServiceType == typeof(ILlmHookPayloadAdapterRegistry));
    }

    [Fact]
    public void AddSeeingCore_ShouldNotRegisterProviderEndpointLookup()
    {
        // 端点查询整组删除：任何叫 IProviderEndpointLookup / OptionsProviderEndpointLookup 的登记都是复活旁路
        var services = CreateServices();

        var endpointDescriptors = services
            .Where(descriptor => descriptor.ServiceType.Name == "IProviderEndpointLookup")
            .ToList();
        var endpointImplementations = services
            .Where(descriptor => descriptor.ImplementationType is not null
                                 && descriptor.ImplementationType.Name == "OptionsProviderEndpointLookup")
            .ToList();

        endpointDescriptors.Should().BeEmpty();
        endpointImplementations.Should().BeEmpty();

        typeof(Seeing.Agent.Core.Llm.LlmRouter).Assembly
            .GetType("Seeing.Agent.Core.Llm.OptionsProviderEndpointLookup")
            .Should().BeNull();
    }

    [Fact]
    public void AdapterRegistry_ShouldServeFiveBuiltInCapabilities()
    {
        var services = CreateServices();
        using var provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<ILlmHookPayloadAdapterRegistry>();

        foreach (var capability in new[]
                 {
                     LlmCapabilityKind.Chat,
                     LlmCapabilityKind.Embedding,
                     LlmCapabilityKind.Decision,
                     LlmCapabilityKind.SpeechSynthesis,
                     LlmCapabilityKind.SpeechTranscription
                 })
        {
            var adapter = registry.Resolve(capability);
            adapter.Should().NotBeNull();
            adapter.Capability.Should().Be(capability);
        }
    }

    [Fact]
    public void AdapterRegistry_ShouldFallBack_WithoutThrowing_ForContractOnlyCapabilities()
    {
        var services = CreateServices();
        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<ILlmHookPayloadAdapterRegistry>();

        var adapter = registry.Resolve(LlmCapabilityKind.VideoGeneration);
        adapter.Should().BeOfType<FallbackLlmHookPayloadAdapter>();

        var payload = adapter.Build(new LlmPayloadContext
        {
            Capability = LlmCapabilityKind.VideoGeneration,
            ProviderId = "p",
            ModelId = "p/m"
        });
        payload[Seeing.Agent.Abstractions.Hooks.HookDataContract.LlmAfterComplete.Capability.Name]
            .Should().Be("video.generation");
    }

    [Fact]
    public void AdapterRegistry_ShouldLetLaterRegistrationOverrideBuiltIn()
    {
        // 决议 3：插件注册同能力适配器 → 后注册胜，覆盖内置
        var services = CreateServices();
        services.AddSingleton<ILlmHookPayloadAdapter>(new OverrideChatAdapter());
        using var provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<ILlmHookPayloadAdapterRegistry>();

        registry.Resolve(LlmCapabilityKind.Chat).Should().BeOfType<OverrideChatAdapter>();
    }

    [Fact]
    public void LlmService_ShouldBeRegisteredAsChatFacade_DependingOnInvoker()
    {
        var services = CreateServices();

        var descriptor = services.Single(d => d.ServiceType == typeof(ILlmService));
        descriptor.ImplementationType.Should().Be(typeof(LlmService));

        typeof(LlmService)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(p => p.ParameterType)
            .Should().Contain(typeof(ILlmInvoker), "chat 门面的横切职责已上提到 ILlmInvoker");
    }

    private sealed class OverrideChatAdapter : ILlmHookPayloadAdapter
    {
        public LlmCapabilityKind Capability => LlmCapabilityKind.Chat;

        public IReadOnlyDictionary<string, object?> Build(LlmPayloadContext context)
            => new Dictionary<string, object?> { ["override"] = true };
    }
}
