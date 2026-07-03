using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Extensions;

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
        workspaceRoot ??= Directory.GetCurrentDirectory();
        services.GetRequiredService<GatewayChannelRegistry>().Reload(workspaceRoot);
    }
}
