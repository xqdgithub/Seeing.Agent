using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Llm;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Provider.DeepSeek;
using Xunit;

namespace Seeing.Provider.DeepSeek.Tests;

public class DeepSeekServiceCollectionExtensionsTests
{
    [Fact]
    public async Task AddDeepSeekProvider_RegistersIntoProviderRegistry()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var registry = new ProviderRegistry(NullLogger<ProviderRegistry>.Instance);
        services.AddSingleton<IProviderRegistry>(registry);
        services.AddSingleton(Mock.Of<ILlmClientFactory>());
        services.AddDeepSeekProvider();

        await using var sp = services.BuildServiceProvider();
        var hosted = sp.GetServices<IHostedService>()
            .OfType<DeepSeekProviderHostedService>()
            .Single();
        await hosted.StartAsync(TestContext.Current.CancellationToken);

        registry.GetProvider("deepseek").Should().BeOfType<DeepSeekProvider>();
    }
}
