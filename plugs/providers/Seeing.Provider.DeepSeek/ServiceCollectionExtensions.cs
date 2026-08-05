using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Configuration;

namespace Seeing.Provider.DeepSeek;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDeepSeekProvider(this IServiceCollection services)
    {
        services.TryAddSingleton(sp =>
            new DeepSeekConfigStore(
                sp.GetService<IWorkspaceProvider>(),
                sp.GetRequiredService<ILogger<DeepSeekConfigStore>>()));
        services.TryAddSingleton<DeepSeekModelsClient>();
        services.TryAddSingleton<DeepSeekProvider>();
        services.AddHostedService<DeepSeekProviderHostedService>();
        return services;
    }
}
