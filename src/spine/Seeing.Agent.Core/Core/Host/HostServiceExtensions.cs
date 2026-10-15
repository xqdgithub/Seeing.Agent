using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Interactions;
using Seeing.Agent.Core.Interactions;

namespace Seeing.Agent.Core.Host;

/// <summary>宿主请求服务扩展 - 独立呈现端注册表 + 在途管理器。</summary>
public static class HostServiceExtensions
{
    /// <summary>注册宿主请求交互基础设施（呈现端注册表与在途管理器均为 Singleton）。</summary>
    /// <param name="services">服务集合</param>
    /// <returns>服务集合</returns>
    public static IServiceCollection AddSeeingHostRequests(this IServiceCollection services)
    {
        // 与权限/问答分离的独立实例，避免跨域误判可呈现
        services.AddSingleton<IHostSurfaceRegistry>(_ => new SurfaceRegistry());
        services.AddSingleton<IHostRequestManager, HostRequestManager>();
        return services;
    }
}
