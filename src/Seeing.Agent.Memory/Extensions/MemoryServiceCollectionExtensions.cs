using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Configuration;
using Seeing.Agent.Memory.Core;
using Seeing.Agent.Memory.Integration;

namespace Seeing.Agent.Memory.Extensions;

/// <summary>
/// Memory 服务 DI 注册扩展方法。
/// 参考 ServiceCollectionExtensions.AddSeeingAgent 模式。
/// </summary>
public static class MemoryServiceCollectionExtensions
{
    /// <summary>
    /// 注册 AgentMemory 服务（使用自定义配置）
    /// </summary>
    /// <param name="services">服务集合</param>
    /// <param name="configure">配置回调</param>
    /// <returns>服务集合</returns>
    public static IServiceCollection AddSeeingAgentMemory(
        this IServiceCollection services,
        Action<MemoryOptions>? configure = null)
    {
        // 配置选项
        if (configure != null)
        {
            services.Configure<MemoryOptions>(configure);
        }

        // 注册存储层（Singleton）
        services.AddSingleton<IMemoryRepository, MdMemoryRepository>();
        services.AddSingleton<IMemoryRetriever, MemoryRetriever>();

        // 注册相似度检查器与去重器（Singleton）
        // TextSimilarityChecker 为默认实现，用于 MemoryDeduplicator
        services.AddSingleton<ISimilarityChecker, TextSimilarityChecker>();
        services.AddSingleton<MemoryDeduplicator>();

        // 注册核心服务（Singleton）
        services.AddSingleton<IMemoryManager, MemoryManager>();
        services.AddSingleton<MemoryOrchestrator>();
        services.AddSingleton<MemoryWriteQueue>();

        // 注册工具（Transient）
        services.AddTransient<MemoryTools>();

        // 注册 Hook Handler（Transient）
        services.AddTransient<ChatMemoryHandler>();
        services.AddTransient<ToolMemoryHandler>();

        // 注册 Extension（Singleton）
        services.AddSingleton<IExtension, MemoryExtension>();

        return services;
    }

    /// <summary>
    /// 注册 AgentMemory 服务（使用配置文件）
    /// </summary>
    /// <param name="services">服务集合</param>
    /// <param name="configuration">配置对象</param>
    /// <returns>服务集合</returns>
    public static IServiceCollection AddSeeingAgentMemory(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // 从配置文件读取 Memory 配置
        services.Configure<MemoryOptions>(configuration.GetSection("Memory"));

        return AddSeeingAgentMemory(services);
    }
}