using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Seeing.Agent.Abstractions.Channels;
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
        services.TryAddSingleton<IGatewayChannelHostClient>(sp => sp.GetRequiredService<WebSocketGatewayClient>());
        services.TryAddSingleton<RemoteSessionClient>();
        services.TryAddSingleton<ISessionClient>(sp => sp.GetRequiredService<RemoteSessionClient>());
        services.AddSingleton<WeComSessionTracker>();
        services.AddSingleton<WeComCommandInterceptor>();
        services.AddSingleton<WeComAibotSession>();
        services.AddSingleton<WeComOutboundGovernor>();
        services.AddSingleton<WeComOutboundChannel>();
        services.AddSingleton<WeComConnectionManager>();
        services.AddSingleton<WeComAibotWsClient>();
        services.AddSingleton(sp => new WeComChannelAdapter(
            sp.GetRequiredService<WeComOutboundChannel>(),
            sp.GetRequiredService<WeComAibotWsClient>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<WeComChannelAdapter>>(),
            sp.GetService<IChannelDirectory>()));
        services.AddSingleton<WeComPermissionPolicy>();
        services.AddSingleton<WeComPermissionState>();
        services.AddSingleton<WeComPermissionResponder>();
        services.AddSingleton<WeComActiveStreamRegistry>();
        services.AddSingleton<WeComChannelBridge>();
        services.AddSingleton<IChannelBridge>(sp => sp.GetRequiredService<WeComChannelBridge>());

        return services;
    }
}
