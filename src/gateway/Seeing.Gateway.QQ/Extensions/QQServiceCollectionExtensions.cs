using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Seeing.Agent.Abstractions.Channels;
using Seeing.Gateway.Client;
using Seeing.Gateway.Client.Extensions;
using Seeing.Gateway.QQ.Connection;
using Seeing.Gateway.QQ.Delivery;

namespace Seeing.Gateway.QQ.Extensions;

/// <summary>
/// QQ Channel Bridge DI 注册
/// </summary>
public static class QQServiceCollectionExtensions
{
    public static IServiceCollection AddSeeingQQChannel(
        this IServiceCollection services,
        Action<QQOptions>? configureQQ = null,
        Action<GatewayClientOptions>? configureGateway = null)
    {
        if (configureQQ != null)
            services.Configure(configureQQ);

        services.Configure<GatewayClientOptions>(options =>
        {
            options.Transport = GatewayClientTransport.WebSocket;
            configureGateway?.Invoke(options);
        });

        // IWorkspaceProvider 由 Host / Core 注册；本包不 new 具体实现。
        services.AddHttpClient(QQHttpApiClient.HttpClientName);
        services.AddSeeingGatewayClient();
        services.TryAddSingleton<IGatewayChannelHostClient>(sp => sp.GetRequiredService<WebSocketGatewayClient>());
        services.TryAddSingleton<RemoteSessionClient>();
        services.TryAddSingleton<ISessionClient>(sp => sp.GetRequiredService<RemoteSessionClient>());
        services.AddSingleton<QQPassiveWindowTracker>();
        services.AddSingleton(sp => new QQChannelAdapter(
            sp.GetRequiredService<QQHttpApiClient>(),
            sp.GetRequiredService<QQPassiveWindowTracker>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<QQChannelAdapter>>(),
            sp.GetService<IChannelDirectory>()));
        services.AddSingleton<QQAccessTokenProvider>();
        services.AddSingleton<QQHttpApiClient>();
        services.AddSingleton<QQWebSocketClient>();
        services.AddSingleton<QQSessionTracker>();
        services.AddSingleton<QQCommandInterceptor>();
        services.AddSingleton<QQPermissionPolicy>();
        services.AddSingleton<QQPermissionState>();
        services.AddSingleton<Cards.IQQCardKind, Cards.PermissionCardKind>();
        services.AddSingleton<Cards.QQCardDispatcher>();
        services.AddSingleton<QQPermissionResponder>();
        services.AddSingleton<QQMediaFetcher>();
        services.AddSingleton<QQChannelHealth>();
        services.AddSingleton<QQChannelBridge>();
        services.AddSingleton<IChannelBridge>(sp => sp.GetRequiredService<QQChannelBridge>());

        return services;
    }
}
