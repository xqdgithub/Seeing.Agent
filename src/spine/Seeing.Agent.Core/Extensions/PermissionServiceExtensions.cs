using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Seeing.Agent.Abstractions.Interactions;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Core.Interactions;
using Seeing.Agent.Core.Permission;

namespace Seeing.Agent.Core.Extensions;

/// <summary>
/// 权限服务扩展 - DI 注册入口
/// </summary>
public static class PermissionServiceExtensions
{
    /// <summary>
    /// 注册权限引擎、存储、在途管理器与授权器工厂。
    /// </summary>
    /// <remarks>
    /// <see cref="PermissionRequestManager"/> 依赖 <c>IExecutionEventPublisher</c>。
    /// <c>AddSeeingCore</c> 以 <c>NullExecutionEventPublisher</c>（全部 no-op）兜底注册，
    /// 故 Core-only 宿主解析 Manager 不会失败；真实宿主由 Hosting 的 <c>AddExecutionEngine</c>
    /// 在后注册真实发布器覆盖（解析取最后一个注册）。Manager 仍只被执行链消费。
    /// </remarks>
    /// <param name="services">服务集合</param>
    /// <returns>服务集合</returns>
    public static IServiceCollection AddPermissionService(this IServiceCollection services)
    {
        // 授权存储（记忆 + 白名单目录面）
        services.AddSingleton<IPermissionGrantStore, PermissionGrantStore>();

        // 呈现端登记表（Singleton）：权限专用实例（与 Question 分离，避免 Gateway 误判可呈现）
        services.AddSingleton<IPermissionSurfaceRegistry>(_ => new SurfaceRegistry());

        // 生效开关解析（Service 与 Manager 共用）
        services.AddSingleton<EffectivePermissionPolicy>();

        // 权限服务（规则/策略 API + AuthorizeAsync 资源门编排）
        services.AddSingleton<IPermissionService, PermissionService>();

        // 在途唯一权威 + 授权器工厂
        services.AddSingleton<IPermissionRequestManager, PermissionRequestManager>();
        services.AddSingleton<IPermissionAuthorizerFactory, DefaultPermissionAuthorizerFactory>();

        // 宿主通道兜底：无交互宿主默认拒绝呈现（宿主可 AddSingleton 覆盖）
        services.TryAddSingleton<IPermissionChannel, DenyAllPermissionChannel>();

        return services;
    }
}
