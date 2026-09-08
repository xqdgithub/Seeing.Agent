using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Configuration; // IWorkspaceProvider (Abstractions assembly)

namespace Seeing.Agent.Gateway.Channels;

public static class GatewayChannelRegistryExtensions
{
    public static IServiceCollection AddGatewayChannelRegistry(this IServiceCollection services)
    {
        services.AddSingleton<GatewayChannelRegistry>();
        return services;
    }

    public static void ReloadGatewayChannelRegistry(this IServiceProvider services, string? workspaceRoot = null)
    {
        workspaceRoot ??= services.GetRequiredService<IWorkspaceProvider>().GetProjectRoot();
        services.GetRequiredService<GatewayChannelRegistry>().Reload(workspaceRoot);
    }
}
