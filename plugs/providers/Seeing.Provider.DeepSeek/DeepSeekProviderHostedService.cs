using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Llm;
using Seeing.Agent.Abstractions.Llm;

namespace Seeing.Provider.DeepSeek;

public sealed class DeepSeekProviderHostedService : IHostedService
{
    private readonly DeepSeekProvider _provider;
    private readonly IProviderRegistry _registry;
    private readonly ILogger<DeepSeekProviderHostedService> _logger;

    public DeepSeekProviderHostedService(
        DeepSeekProvider provider,
        IProviderRegistry registry,
        ILogger<DeepSeekProviderHostedService> logger)
    {
        _provider = provider;
        _registry = registry;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _provider.WarmupAsync(cancellationToken).ConfigureAwait(false);
        _registry.Register(_provider, DeepSeekProvider.ExtensionId);
        _logger.LogInformation("已注册 DeepSeek Provider");
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _registry.Unregister(_provider.Id);
        return Task.CompletedTask;
    }
}
