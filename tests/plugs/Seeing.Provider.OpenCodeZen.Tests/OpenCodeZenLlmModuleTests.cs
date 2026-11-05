using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Seeing.Agent.Abstractions.Llm;
using Xunit;

namespace Seeing.Provider.OpenCodeZen.Tests;

public class OpenCodeZenLlmModuleTests
{
    [Fact]
    public async Task Activate_RegistersIntoProviderRegistry_Deactivate_Unregisters()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var registry = new Mock<IProviderRegistry>();
        ILlmProvider? registered = null;
        registry.Setup(r => r.Register(It.IsAny<ProviderRegistration>()))
            .Callback<ProviderRegistration>(r => registered = r.Provider);
        registry.Setup(r => r.Unregister(It.IsAny<string>()))
            .Returns(true)
            .Callback<string>(_ => registered = null);
        services.AddSingleton(registry.Object);

        var factory = new Mock<ILlmClientFactory>();
        services.AddSingleton(factory.Object);
        services.AddSingleton<ILlmClientFactoryResolver>(new LlmClientFactoryResolver(
            [new LlmClientFactoryRegistration(ProviderTypes.OpenAi, factory.Object)]));

        var capability = new Mock<IModelCapabilityManager>();
        capability.Setup(m => m.TryEnrichIfEnabledAsync(It.IsAny<ModelConfig>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ModelConfig m, CancellationToken _) => m);
        services.AddSingleton(capability.Object);

        var module = new OpenCodeZenLlmModule();
        module.Id.Should().Be("provider.opencodezen");
        module.DependsOn.Should().Contain("llm.openai");
        module.ConfigureServices(services);

        await using var sp = services.BuildServiceProvider();
        await module.ActivateAsync(sp, TestContext.Current.CancellationToken);

        registered.Should().BeOfType<OpenCodeZenProvider>();
        registry.Verify(r => r.Register(It.Is<ProviderRegistration>(x => x.Provider is OpenCodeZenProvider && x.OwnerId == OpenCodeZenProvider.ExtensionId)), Times.Once);

        await module.DeactivateAsync(sp, TestContext.Current.CancellationToken);
        registry.Verify(r => r.Unregister("opencode-zen"), Times.Once);
        registered.Should().BeNull();
    }
}
