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
    /// 注册 SessionManager 服务
    /// </summary>
    /// <param name="services">服务集合</param>
    /// <param name="storagePath">存储路径（默认 ~/.seeing/sessions）</param>
    /// <param name="customStore">自定义存储实现（优先于 storagePath）</param>
    /// <returns>服务集合</returns>
    /// <remarks>
    /// 使用方式：
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
        services.AddSingleton<ISessionManager>(sp =>
        {
            // 创建存储实例（优先使用自定义存储）
            var store = customStore ?? new FileSessionStore(storagePath);

            return new SessionManager(
                store: store,
                compressor: sp.GetService<ICompressionStrategy>(),
                hookManager: sp.GetService<IHookManager>(),
                eventPublisher: sp.GetService<ISessionEventPublisher>(),
                logger: sp.GetService<ILogger<SessionManager>>(),
                forker: null,    // SessionManager 内部不自动创建
                archiver: null,
                sharer: null,
                reverter: null,
                globalStore: sp.GetService<GlobalSessionStore>());
        });

        return services;
    }
}
