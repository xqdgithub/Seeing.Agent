using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Extensions;
using Xunit;

namespace Seeing.Agent.Tests.Extensions;

/// <summary>
/// P5-T7：公共 API 形状 — 删 AddSeeingAgent；AddSeeingCore/AddSeeingModule 共享 registry。
/// </summary>
public class PublicApiShapeTests
{
    [Fact]
    public void AddSeeingAgent_Should_Not_Exist()
    {
        var methods = typeof(ServiceCollectionExtensions)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "AddSeeingAgent")
            .ToList();

        methods.Should().BeEmpty("AddSeeingAgent 已删除，宿主须改用 AddSeeingCore(registry)");
    }

    [Fact]
    public void InitializeSeeingAgentAsync_Should_Not_Exist()
    {
        var methods = typeof(SeeingAgentInitializationExtensions)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "InitializeSeeingAgentAsync")
            .ToList();

        methods.Should().BeEmpty("已改名为 InitializeSeeingAsync");
    }

    [Fact]
    public void AddSeeingCore_Signature_Should_Require_IConfigSectionRegistry()
    {
        var overloads = typeof(ServiceCollectionExtensions)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == nameof(ServiceCollectionExtensions.AddSeeingCore))
            .ToList();

        overloads.Should().NotBeEmpty();
        foreach (var method in overloads)
        {
            var parameters = method.GetParameters();
            parameters.Should().Contain(p => p.ParameterType == typeof(IConfigSectionRegistry),
                "AddSeeingCore 所有重载须接受 IConfigSectionRegistry");
        }
    }

    [Fact]
    public void AddSeeingModule_Signature_Should_Require_IConfigSectionRegistry()
    {
        var method = typeof(ServiceCollectionExtensions)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == nameof(ServiceCollectionExtensions.AddSeeingModule));

        method.IsGenericMethodDefinition.Should().BeTrue();
        method.GetParameters().Should().Contain(p => p.ParameterType == typeof(IConfigSectionRegistry));
    }

    [Fact]
    public void InitializeSeeingAsync_Should_Exist()
    {
        var method = typeof(SeeingAgentInitializationExtensions)
            .GetMethod(nameof(SeeingAgentInitializationExtensions.InitializeSeeingAsync),
                BindingFlags.Public | BindingFlags.Static);

        method.Should().NotBeNull();
    }

    [Fact]
    public void AddSeeingModule_Should_Register_ISeeingModule_And_Share_Registry()
    {
        var services = new ServiceCollection();
        var registry = new ConfigSectionRegistry();
        services.AddSingleton<IConfigSectionRegistry>(registry);

        services.AddSeeingModule<StubModule>(registry);

        // Phase 8：经 factory 注入可选 IUiContributionRegistry，不再用 ImplementationInstance
        var moduleDescriptor = services.Single(d => d.ServiceType == typeof(ISeeingModule));
        moduleDescriptor.ImplementationFactory.Should().NotBeNull();
        moduleDescriptor.ImplementationInstance.Should().BeNull();

        using var sp = services.BuildServiceProvider();
        sp.GetRequiredService<ISeeingModule>().Should().BeOfType<StubModule>();

        services.GetOrCreateConfigSectionRegistry().Should().BeSameAs(registry);
    }

    private sealed class StubModule : ISeeingModule
    {
        public string Id => "stub.test";
        public IReadOnlyList<string> ProvidedTools => [];
        public IReadOnlyList<string> ProvidedSeams => [];
        public IReadOnlyList<string> DependsOn => [];
        public void ConfigureServices(IServiceCollection services) { }
        public Task ActivateAsync(IServiceProvider services, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeactivateAsync(IServiceProvider services, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
