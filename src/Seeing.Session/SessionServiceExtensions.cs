using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seeing.Session.Compression;
using Seeing.Session.Core;
using Seeing.Session.Hooks;
using Seeing.Session.Management;
using Seeing.Session.Storage;

namespace Seeing.Session;

/// <summary>
/// Session 服务扩展方法
/// </summary>
public static class SessionServiceExtensions
{
    /// <summary>
    /// 注册 SessionManager 服务（带 Store，具体类型与接口为同一实例）
    /// </summary>
    /// <param name="services">服务集合</param>
    /// <param name="storagePath">存储路径（默认 ~/.seeing/sessions）</param>
    /// <param name="customStore">自定义存储实现（优先于 storagePath）</param>
    /// <returns>服务集合</returns>
    /// <remarks>
    /// 与 <c>AddSeeingAgent</c> 的关系：若已通过 AddSeeingAgent 注册了
    /// <see cref="ISessionManager"/>，本方法为 no-op，避免双实例分裂。
    /// <para>
    /// 使用方式：
    /// </para>
    /// <code>
    /// // 默认存储
    /// services.AddSessionManager();
    ///
    /// // 指定路径
    /// services.AddSessionManager(storagePath: "/data/sessions");
    ///
    /// // 自定义存储
    /// services.AddSessionManager(customStore: new DatabaseSessionStore());
    /// </code>
    /// </remarks>
    public static IServiceCollection AddSessionManager(
        this IServiceCollection services,
        string? storagePath = null,
        ISessionStore? customStore = null)
    {
        // AddSeeingAgent 已注册时不再覆盖，保证 I1：SessionManager 与 ISessionManager 同一引用
        if (services.Any(d => d.ServiceType == typeof(ISessionManager)))
            return services;

        if (!services.Any(d => d.ServiceType == typeof(ISessionStore)))
        {
            services.AddSingleton<ISessionStore>(_ =>
                customStore ?? new FileSessionStore(storagePath));
        }

        services.AddSingleton<SessionManager>(sp =>
        {
            var store = customStore
                ?? sp.GetRequiredService<ISessionStore>();

            return new SessionManager(
                store: store,
                compressor: sp.GetService<ICompressionStrategy>(),
                hookManager: sp.GetService<IHookManager>(),
                eventPublisher: sp.GetService<ISessionEventPublisher>(),
                logger: sp.GetService<ILogger<SessionManager>>(),
                forker: null,
                archiver: null,
                sharer: null,
                reverter: null,
                globalStore: sp.GetService<GlobalSessionStore>());
        });
        services.AddSingleton<ISessionManager>(sp =>
            sp.GetRequiredService<SessionManager>());

        return services;
    }
}
