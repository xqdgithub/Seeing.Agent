using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Llm;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Provider.OpenCodeZen;
using Xunit;

namespace Seeing.Provider.OpenCodeZen.Tests;

public class OpenCodeZenServiceCollectionExtensionsTests
{
    [Fact]
    public async Task AddOpenCodeZenProvider_RegistersIntoProviderRegistry()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var registry = new ProviderRegistry(NullLogger<ProviderRegistry>.Instance);
        services.AddSingleton<IProviderRegistry>(registry);
        services.AddSingleton(Mock.Of<ILlmClientFactory>());
        services.AddOpenCodeZenProvider();

        await using var sp = services.BuildServiceProvider();
        var hosted = sp.GetServices<IHostedService>()
            .OfType<OpenCodeZenProviderHostedService>()
            .Single();
        await hosted.StartAsync(TestContext.Current.CancellationToken);

        registry.GetProvider("opencode-zen").Should().BeOfType<OpenCodeZenProvider>();
    }
}
