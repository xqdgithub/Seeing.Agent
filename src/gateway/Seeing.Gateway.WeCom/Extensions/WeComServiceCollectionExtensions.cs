using Microsoft.Extensions.DependencyInjection;
using Seeing.Gateway.Client;
using Seeing.Gateway.Client.Extensions;
using Seeing.Gateway.WeCom.Connection;

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

        // IWorkspaceProvider 由 Host / Core 注册；本包不 new 具体实现。
        services.AddHttpClient(WeComMediaFetcher.HttpClientName);
        services.AddSingleton<WeComMediaFetcher>();
        services.AddSeeingGatewayClient();
        services.AddSingleton<WeComSessionTracker>();
        services.AddSingleton<WeComCommandInterceptor>();
        services.AddSingleton<WeComAibotSession>();
        services.AddSingleton<WeComOutboundGovernor>();
        services.AddSingleton<WeComOutboundChannel>();
        services.AddSingleton<WeComConnectionManager>();
        services.AddSingleton<WeComAibotWsClient>();
        services.AddSingleton<WeComPermissionPolicy>();
        services.AddSingleton<WeComPermissionState>();
        services.AddSingleton<WeComPermissionResponder>();
        services.AddSingleton<WeComActiveStreamRegistry>();
        services.AddSingleton<WeComChannelBridge>();
        services.AddSingleton<IChannelBridge>(sp => sp.GetRequiredService<WeComChannelBridge>());

        return services;
    }
}
