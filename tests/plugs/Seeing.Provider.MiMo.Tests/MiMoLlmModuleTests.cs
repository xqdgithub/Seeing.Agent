using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Seeing.Agent.Abstractions.Llm;
using Xunit;

namespace Seeing.Provider.MiMo.Tests;

public class MiMoLlmModuleTests
{
    [Fact]
    public async Task Activate_RegistersIntoProviderRegistry_Deactivate_Unregisters()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var registry = new Mock<IProviderRegistry>();
        ILlmProvider? registered = null;
        registry.Setup(r => r.Register(It.IsAny<ILlmProvider>(), It.IsAny<string?>()))
            .Callback<ILlmProvider, string?>((p, _) => registered = p);
        registry.Setup(r => r.Unregister(It.IsAny<string>()))
            .Returns(true)
            .Callback<string>(_ => registered = null);
        services.AddSingleton(registry.Object);

        var factory = new Mock<ILlmClientFactory>();
        factory.Setup(f => f.SupportsType(ProviderTypes.OpenAi)).Returns(true);
        services.AddSingleton(factory.Object);

        var capability = new Mock<IModelCapabilityManager>();
        capability.Setup(m => m.TryEnrichIfEnabledAsync(It.IsAny<ModelConfig>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ModelConfig m, CancellationToken _) => m);
        services.AddSingleton(capability.Object);

        var module = new MiMoLlmModule();
        module.Id.Should().Be("provider.mimo");
        module.DependsOn.Should().Contain("llm.openai");
        module.ConfigureServices(services);

        await using var sp = services.BuildServiceProvider();
        await module.ActivateAsync(sp, TestContext.Current.CancellationToken);

        registered.Should().BeOfType<MiMoProvider>();
        registry.Verify(r => r.Register(
            It.IsAny<MiMoProvider>(),
            MiMoProvider.ExtensionId), Times.Once);

        await module.DeactivateAsync(sp, TestContext.Current.CancellationToken);
        registry.Verify(r => r.Unregister("mimo"), Times.Once);
        registered.Should().BeNull();
    }
}
