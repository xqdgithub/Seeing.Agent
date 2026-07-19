using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Seeing.Agent.Configuration;
using Seeing.Gateway.Client;
using Seeing.Gateway.Client.Extensions;
using Seeing.Gateway.QQ.Connection;

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

        services.TryAddSingleton<IWorkspaceProvider>(_ => new WorkspaceProvider());
        services.AddHttpClient(QQHttpApiClient.HttpClientName);
        services.AddSeeingGatewayClient();
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
