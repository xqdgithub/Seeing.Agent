using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Core.Permission;
using Seeing.Agent.Core.Interfaces;

namespace Seeing.Agent.Extensions;

/// <summary>
/// 权限服务扩展 - DI 注册入口
/// </summary>
public static class PermissionServiceExtensions
{
    /// <summary>
    /// 注册权限服务
    /// </summary>
    /// <param name="services">服务集合</param>
    /// <returns>服务集合</returns>
    public static IServiceCollection AddPermissionService(this IServiceCollection services)
    {
        // 注册权限缓存
        services.AddSingleton<IPermissionCache, PermissionCache>();
        
        // 注册权限服务
        services.AddSingleton<IPermissionService, PermissionService>();
        
        return services;
    }
}
