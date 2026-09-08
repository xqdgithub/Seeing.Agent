using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Gateway.Configuration;
using Seeing.Agent.Gateway.Extensions;

namespace Seeing.Agent.Hosting.Gateway;

/// <summary>
/// Gateway Host Shape DI 扩展（Kestrel Gateway 服务端宿主表面）。
/// </summary>
public static class GatewayHostingServiceCollectionExtensions
{
    /// <summary>
    /// 注册 Gateway Host Shape：声明可承载模块类型，并转发到 <c>AddSeeingGatewayServer</c>。
    /// 能力包（Tools.* 等）由宿主 sample 自行引用。
    /// </summary>
    public static IServiceCollection AddSeeingHostingGateway(
        this IServiceCollection services,
        IConfigSectionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        services.AddSingleton(GatewayHostShape.Descriptor);
        return services.AddSeeingGatewayServer(registry);
    }

    /// <summary>
    /// 注册 Gateway Host Shape（兼容配置参数重载）。
    /// </summary>
    public static IServiceCollection AddSeeingHostingGateway(
        this IServiceCollection services,
        IConfigSectionRegistry registry,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(registry);
        services.AddSingleton(GatewayHostShape.Descriptor);
        return services.AddSeeingGatewayServer(registry, configuration);
    }

    /// <summary>
    /// 注册 Gateway Host Shape（委托配置 GatewayOptions）。
    /// </summary>
    public static IServiceCollection AddSeeingHostingGateway(
        this IServiceCollection services,
        IConfigSectionRegistry registry,
        Action<GatewayOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(configure);
        services.AddSingleton(GatewayHostShape.Descriptor);
        return services.AddSeeingGatewayServer(registry, configure);
    }
}
