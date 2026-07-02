using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Seeing.Gateway.Client;
using Seeing.Gateway.Client.Extensions;

namespace Seeing.Gateway.WeCom.Extensions;

/// <summary>
/// 企微 Channel Bridge DI 注册
/// </summary>
public static class WeComServiceCollectionExtensions
{
    public static IServiceCollection AddSeeingWeComChannel(
        this IServiceCollection services,
        Action<WeComOptions>? configureWeCom = null,
        Action<GatewayClientOptions>? configureGateway = null)
    {
        if (configureWeCom != null)
            services.Configure(configureWeCom);

        services.Configure<GatewayClientOptions>(options =>
        {
            options.Transport = GatewayClientTransport.WebSocket;
            configureGateway?.Invoke(options);
        });

        services.AddHttpClient<WeComMediaFetcher>();
        services.AddSeeingGatewayClient();
        services.AddSingleton<WeComAibotWsClient>();
        services.AddSingleton<WeComPermissionPolicy>();
        services.AddSingleton<WeComPermissionState>();
        services.AddSingleton<WeComChannelBridge>();
        services.AddSingleton<IChannelBridge>(sp => sp.GetRequiredService<WeComChannelBridge>());

        return services;
    }
}
