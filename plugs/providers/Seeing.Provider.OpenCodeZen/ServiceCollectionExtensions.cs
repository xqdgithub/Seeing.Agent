using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Configuration;

namespace Seeing.Provider.OpenCodeZen;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddOpenCodeZenProvider(this IServiceCollection services)
    {
        services.TryAddSingleton(sp =>
            new OpenCodeZenConfigStore(
                sp.GetService<IWorkspaceProvider>(),
                sp.GetRequiredService<ILogger<OpenCodeZenConfigStore>>()));
        services.TryAddSingleton<OpenCodeZenModelsClient>();
        services.TryAddSingleton<OpenCodeZenProvider>();
        services.AddHostedService<OpenCodeZenProviderHostedService>();
        return services;
    }
}
