using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Llm;
using Seeing.Agent.Abstractions.Llm;

namespace Seeing.Provider.OpenCodeZen;

public sealed class OpenCodeZenProviderHostedService : IHostedService
{
    private readonly OpenCodeZenProvider _provider;
    private readonly IProviderRegistry _registry;
    private readonly ILogger<OpenCodeZenProviderHostedService> _logger;

    public OpenCodeZenProviderHostedService(
        OpenCodeZenProvider provider,
        IProviderRegistry registry,
        ILogger<OpenCodeZenProviderHostedService> logger)
    {
        _provider = provider;
        _registry = registry;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _provider.WarmupAsync(cancellationToken).ConfigureAwait(false);
        _registry.Register(_provider, OpenCodeZenProvider.ExtensionId);
        _logger.LogInformation("已注册 OpenCode Zen Provider");
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _registry.Unregister(_provider.Id);
        return Task.CompletedTask;
    }
}
