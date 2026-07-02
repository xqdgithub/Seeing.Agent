using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Seeing.Gateway.Client.Extensions;

/// <summary>
/// Gateway Client DI 注册扩展
/// </summary>
public static class GatewayClientServiceCollectionExtensions
{
    /// <summary>
    /// 注册 <see cref="IGatewayClient"/>（按 Transport 选择 HTTP 或 WebSocket）
    /// </summary>
    public static IServiceCollection AddSeeingGatewayClient(
        this IServiceCollection services,
        Action<GatewayClientOptions>? configure = null)
    {
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.AddHttpClient<HttpGatewayClient>();

        services.AddSingleton<WebSocketGatewayClient>();
        services.AddSingleton<IGatewayClient>(sp =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<GatewayClientOptions>>().Value;
            return options.Transport == GatewayClientTransport.WebSocket
                ? new WebSocketGatewayClientAdapter(sp.GetRequiredService<WebSocketGatewayClient>())
                : sp.GetRequiredService<HttpGatewayClient>();
        });

        return services;
    }

    /// <summary>
    /// 从配置节 Gateway 注册 <see cref="IGatewayClient"/>
    /// </summary>
    public static IServiceCollection AddSeeingGatewayClient(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<GatewayClientOptions>(configuration.GetSection(GatewayClientOptions.SectionName));
        return AddSeeingGatewayClient(services);
    }
}
