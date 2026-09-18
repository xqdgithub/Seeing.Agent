using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Session.Core;
using Seeing.Session.Hooks;
using Seeing.Session.Management;
using Seeing.Session.Persistence;
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
    /// 与 <c>AddSeeingCore</c> 的关系：若已通过 AddSeeingCore 注册了
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
        // 会话持久化写回配置（共享单例；调用方可预先注册实例以调整，默认启用写回）
        // 以具体实例注册，使下方基于 ImplementationInstance 的 Enabled 探测真正生效。
        // 如需旁路写回：在调用 AddSessionManager 之前注册
        // new SessionPersistenceOptions { Enabled = false } 实例。
        services.TryAddSingleton(new SessionPersistenceOptions());

        // 写回开关：调用方在注册前提供 SessionPersistenceOptions 实例并设置 Enabled=false 可旁路装饰器
        var writeBehindEnabled = (services
            .LastOrDefault(d => d.ServiceType == typeof(SessionPersistenceOptions))
            ?.ImplementationInstance as SessionPersistenceOptions)?.Enabled ?? true;

        if (!services.Any(d => d.ServiceType == typeof(ISessionStore)))
        {
            if (writeBehindEnabled)
            {
                // 具体装饰器单例 + 接口映射到同一实例：刷新状态共享；容器对具体单例仅释放一次，
                // 调度器 Dispose 内部 Interlocked 幂等，重复捕获亦安全
                services.AddSingleton<WriteBehindSessionStore>(sp =>
                {
                    var inner = customStore ?? new FileSessionStore(storagePath);
                    var options = sp.GetRequiredService<SessionPersistenceOptions>();
                    return new WriteBehindSessionStore(
                        inner,
                        options,
                        sp.GetService<ILogger<WriteBehindSessionStore>>());
                });
                services.AddSingleton<ISessionStore>(sp =>
                    sp.GetRequiredService<WriteBehindSessionStore>());
                services.AddSingleton<IWriteBehindSessionStore>(sp =>
                    sp.GetRequiredService<WriteBehindSessionStore>());
            }
            else
            {
                services.AddSingleton<ISessionStore>(_ =>
                    customStore ?? new FileSessionStore(storagePath));
            }
        }

        // AddSeeingCore 已注册 ISessionManager 时不再覆盖，保证 I1：SessionManager 与 ISessionManager 同一引用
        if (!services.Any(d => d.ServiceType == typeof(ISessionManager)))
        {
            services.AddSingleton<SessionManager>(sp =>
            {
                return new SessionManager(
                    store: sp.GetRequiredService<ISessionStore>(),
                    hookManager: sp.GetService<IHookManager>(),
                    eventPublisher: sp.GetService<ISessionEventPublisher>(),
                    logger: sp.GetService<ILogger<SessionManager>>(),
                    archiver: null,
                    sharer: null,
                    reverter: null,
                    catalog: sp.GetService<ISessionCatalog>());
            });
            services.AddSingleton<ISessionManager>(sp =>
                sp.GetRequiredService<SessionManager>());
        }

        // 会话组存储 / 分支器 / 组管理器（关系唯一权威）：与 Core 共用存在性检查，避免双实例
        if (!services.Any(d => d.ServiceType == typeof(ISessionGroupStore)))
        {
            if (writeBehindEnabled)
            {
                // 具体装饰器单例 + 接口映射到同一实例：刷新状态共享；容器对具体单例仅释放一次，
                // 调度器 Dispose 内部 Interlocked 幂等，重复捕获亦安全
                services.AddSingleton<WriteBehindSessionGroupStore>(sp =>
                {
                    var options = sp.GetRequiredService<SessionPersistenceOptions>();
                    return new WriteBehindSessionGroupStore(
                        new FileSessionGroupStore(),
                        options,
                        sp.GetService<ILogger<WriteBehindSessionGroupStore>>());
                });
                services.AddSingleton<ISessionGroupStore>(sp =>
                    sp.GetRequiredService<WriteBehindSessionGroupStore>());
                services.AddSingleton<IWriteBehindSessionGroupStore>(sp =>
                    sp.GetRequiredService<WriteBehindSessionGroupStore>());
            }
            else
            {
                services.AddSingleton<ISessionGroupStore>(_ => new FileSessionGroupStore());
            }
        }

        services.TryAddSingleton(sp =>
            new SessionForker(
                sp.GetService<ILogger<SessionForker>>() ?? NullLogger<SessionForker>.Instance,
                sp.GetRequiredService<ISessionManager>()));

        services.TryAddSingleton(sp =>
            new SessionGroupManager(
                sp.GetRequiredService<ISessionManager>(),
                sp.GetRequiredService<ISessionGroupStore>(),
                sp.GetRequiredService<SessionForker>(),
                sp.GetService<ILogger<SessionGroupManager>>()));
        services.TryAddSingleton<ISessionGroupManager>(sp =>
            sp.GetRequiredService<SessionGroupManager>());

        return services;
    }
}
