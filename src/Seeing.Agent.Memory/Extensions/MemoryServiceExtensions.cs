using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Background;
using Seeing.Agent.Memory.Core;
using Seeing.Agent.Memory.Core.CostControl;
using Seeing.Agent.Memory.Core.Embedding;
using Seeing.Agent.Memory.Core.Graph;
using Seeing.Agent.Memory.Core.Index;
using Seeing.Agent.Memory.Integration;

namespace Seeing.Agent.Memory.Extensions;

/// <summary>
/// Memory 服务 DI 注册扩展
/// </summary>
public static class MemoryServiceExtensions
{
    /// <summary>
    /// 添加 Memory 服务
    /// </summary>
    /// <param name="services">服务集合</param>
    /// <param name="connectionString">SQLite 连接字符串</param>
    /// <returns></returns>
    public static IServiceCollection AddMemoryServices(
        this IServiceCollection services,
        string connectionString = "Data Source=memory.db")
    {
        // 注册 SQLite 连接（单例，共享连接）
        services.TryAddSingleton(sp =>
        {
            var connection = new SqliteConnection(connectionString);
            connection.Open();
            return connection;
        });

        // 注册存储服务
        services.TryAddSingleton<IFileStore, Core.Storage.LocalFileStore>();

        // 注册默认 Embedding 服务（随机向量，用于开发/测试）
        services.TryAddSingleton<IEmbeddingService, RandomEmbeddingService>();

        // 注册索引服务
        services.TryAddSingleton<IVectorIndex>(sp =>
        {
            var connection = sp.GetRequiredService<SqliteConnection>();
            var embeddingService = sp.GetRequiredService<IEmbeddingService>();
            var logger = sp.GetRequiredService<ILogger<VectorIndex>>();
            return new VectorIndex(connection, embeddingService, logger);
        });

        services.TryAddSingleton<IKeywordIndex>(sp =>
        {
            var connection = sp.GetRequiredService<SqliteConnection>();
            var logger = sp.GetRequiredService<ILogger<KeywordIndex>>();
            return new KeywordIndex(connection, logger);
        });

        services.TryAddSingleton<IMemoryIndex, HybridMemoryIndex>();

        // 注册图谱服务
        services.TryAddSingleton<IMemoryGraph>(sp =>
        {
            var connection = sp.GetRequiredService<SqliteConnection>();
            var logger = sp.GetRequiredService<ILogger<SqliteMemoryGraph>>();
            return new SqliteMemoryGraph(connection, logger);
        });

        // 注册缓存服务
        services.TryAddSingleton<IEmbeddingCache>(sp =>
        {
            var connection = sp.GetRequiredService<SqliteConnection>();
            var logger = sp.GetRequiredService<ILogger<SqliteEmbeddingCache>>();
            return new SqliteEmbeddingCache(connection, logger);
        });

        // 注册成本控制服务
        services.TryAddSingleton<IRateLimiter>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<TokenBucketRateLimiter>>();
            return new TokenBucketRateLimiter(maxTokens: 100, refillRate: 10.0, logger);
        });

        services.TryAddSingleton<ITokenTracker>(sp =>
        {
            var connection = sp.GetRequiredService<SqliteConnection>();
            var logger = sp.GetRequiredService<ILogger<SqliteTokenTracker>>();
            return new SqliteTokenTracker(connection, logger);
        });

        services.TryAddSingleton<IQuotaManager>(sp =>
        {
            var connection = sp.GetRequiredService<SqliteConnection>();
            var logger = sp.GetRequiredService<ILogger<DailyQuotaManager>>();
            return new DailyQuotaManager(connection, logger);
        });

        // 注册统一服务
        services.TryAddScoped<IMemoryService, MemoryService>();

        // 注册 Hook Handler（自动捕获对话和工具结果）
        services.TryAddScoped<ChatMemoryHandler>();
        services.TryAddScoped<ToolMemoryHandler>();

        // 注册后台服务
        services.AddHostedService<MemoryIndexingService>();

        return services;
    }

    /// <summary>
    /// 添加 Memory 服务（带自定义 Embedding 服务）
    /// </summary>
    public static IServiceCollection AddMemoryServices<TEmbedding>(
        this IServiceCollection services,
        string connectionString = "Data Source=memory.db")
        where TEmbedding : class, IEmbeddingService
    {
        services.AddMemoryServices(connectionString);

        // 注册自定义 Embedding 服务
        services.TryAddSingleton<TEmbedding>();
        services.TryAddSingleton<IEmbeddingService>(sp =>
        {
            var inner = sp.GetRequiredService<TEmbedding>();
            var cache = sp.GetService<IEmbeddingCache>();
            var logger = sp.GetRequiredService<ILogger<EmbeddingService>>();
            return new EmbeddingService(inner, cache, logger);
        });

        return services;
    }
}
