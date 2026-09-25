using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Modules;
using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Background;
using Seeing.Agent.Memory.Configuration;
using Seeing.Agent.Memory.Core;
using Seeing.Agent.Memory.Core.CostControl;
using Seeing.Agent.Memory.Core.Embedding;
using Seeing.Agent.Memory.Core.Graph;
using Seeing.Agent.Memory.Core.Index;
using Seeing.Agent.Memory.Integration;
using Seeing.Agent.Memory.Integration.Adapters;
using Seeing.Agent.Memory.Integration.Hosting;
using Seeing.Agent.Memory.Integration.Tools;
using Seeing.Session.Core;

namespace Seeing.Agent.Memory.Extensions;

/// <summary>
/// Memory 服务 DI 注册扩展。宿主只需调用本方法；Tool/Hook 由模块自注册。
/// </summary>
public static class MemoryServiceExtensions
{
    public static IServiceCollection AddMemoryServices(
        this IServiceCollection services,
        IConfigSectionRegistry registry,
        string? connectionString = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        services.EnsureConfigSectionRegistry(registry);
        registry.Register(
            new ConfigSectionMeta(
                ConfigSectionMemoryOptionsStore.SectionName,
                "memory.json",
                ConfigScope.Both,
                typeof(MemoryOptions)));

        services.TryAddSingleton<MemoryOptionsProvider>();
        services.TryAddSingleton<IMemoryOptionsStore>(sp => sp.GetRequiredService<MemoryOptionsProvider>());

        // 配置重载处理器（依赖 MemoryOptionsProvider，必须在其后注册）
        services.AddSingleton<IReloadHandler, MemoryReloadHandler>();
        services.TryAddSingleton<IOptions<MemoryOptions>>(sp =>
            new MemoryOptionsAccessor(sp.GetRequiredService<MemoryOptionsProvider>()));
        services.TryAddSingleton<IOptionsMonitor<MemoryOptions>>(sp =>
            sp.GetRequiredService<MemoryOptionsProvider>());
        services.TryAddSingleton<IEmbeddingConnectionTester, EmbeddingConnectionTester>();

        services.TryAddSingleton<IMemorySessionEvents>(sp =>
        {
            var publisher = sp.GetService<ISessionEventPublisher>();
            return publisher is null
                ? new NullMemorySessionEvents()
                : new SessionEventPublisherAdapter(publisher);
        });

        services.TryAddSingleton<IMemoryWorkQueue>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<MemoryOptions>>().Value;
            return new Core.Queue.ChannelMemoryWorkQueue(options.Capture.QueueCapacity);
        });
        services.TryAddSingleton<IMemoryHeuristicFilter, Core.Filter.HeuristicMemoryFilter>();

        // 空壳 Owner：Activate 才 Open，Deactivate Close；不把 SqliteConnection 登记为 DI Singleton。
        services.TryAddSingleton(sp =>
            new SqliteConnectionOwner(() => ResolveConnectionString(sp, connectionString)));
        services.TryAddSingleton<SqliteConnectionGate>();
        services.TryAddSingleton<MemoryModuleActivity>();
        services.AddSingleton<ISeeingModule>(sp => new MemoryModule(
            sp.GetRequiredService<MemoryModuleActivity>(),
            sp.GetRequiredService<SqliteConnectionOwner>(),
            sp.GetService<Seeing.Agent.Abstractions.Ui.IUiContributionRegistry>()));

        services.TryAddSingleton<IFileStore, Core.Storage.LocalFileStore>();

        services.TryAddSingleton<IEmbeddingStatus, ConfigurableEmbeddingStatus>();
        services.TryAddSingleton<IEmbeddingService>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<MemoryOptions>>().Value;
            if (options.IsEmbeddingConfigured && sp.GetService<IHttpClientFactory>() is not null)
            {
                var inner = ActivatorUtilities.CreateInstance<ProviderEmbeddingService>(sp);
                var cache = sp.GetService<IEmbeddingCache>();
                var logger = sp.GetService<ILogger<EmbeddingService>>();
                return new EmbeddingService(inner, cache, logger);
            }
            return new NullEmbeddingService();
        });

        services.TryAddSingleton<IVectorIndex>(sp =>
        {
            var owner = sp.GetRequiredService<SqliteConnectionOwner>();
            var gate = sp.GetRequiredService<SqliteConnectionGate>();
            var embeddingService = sp.GetRequiredService<IEmbeddingService>();
            var logger = sp.GetRequiredService<ILogger<VectorIndex>>();
            return new VectorIndex(owner, gate, embeddingService, logger);
        });

        services.TryAddSingleton<IKeywordIndex>(sp =>
        {
            var owner = sp.GetRequiredService<SqliteConnectionOwner>();
            var gate = sp.GetRequiredService<SqliteConnectionGate>();
            var logger = sp.GetRequiredService<ILogger<KeywordIndex>>();
            return new KeywordIndex(owner, gate, logger);
        });

        services.TryAddSingleton<IMemoryIndex, HybridMemoryIndex>();

        services.TryAddSingleton<IMemoryGraph>(sp =>
        {
            var owner = sp.GetRequiredService<SqliteConnectionOwner>();
            var gate = sp.GetRequiredService<SqliteConnectionGate>();
            var logger = sp.GetRequiredService<ILogger<SqliteMemoryGraph>>();
            return new SqliteMemoryGraph(owner, gate, logger);
        });

        services.TryAddSingleton<IEmbeddingCache>(sp =>
        {
            var owner = sp.GetRequiredService<SqliteConnectionOwner>();
            var gate = sp.GetRequiredService<SqliteConnectionGate>();
            var logger = sp.GetRequiredService<ILogger<SqliteEmbeddingCache>>();
            return new SqliteEmbeddingCache(owner, gate, logger);
        });

        services.TryAddSingleton<IRateLimiter>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<TokenBucketRateLimiter>>();
            return new TokenBucketRateLimiter(maxTokens: 100, refillRate: 10.0, logger);
        });

        services.TryAddSingleton<ITokenTracker>(sp =>
        {
            var owner = sp.GetRequiredService<SqliteConnectionOwner>();
            var gate = sp.GetRequiredService<SqliteConnectionGate>();
            var logger = sp.GetRequiredService<ILogger<SqliteTokenTracker>>();
            return new SqliteTokenTracker(owner, gate, logger);
        });

        services.TryAddSingleton<IQuotaManager>(sp =>
        {
            var owner = sp.GetRequiredService<SqliteConnectionOwner>();
            var gate = sp.GetRequiredService<SqliteConnectionGate>();
            var logger = sp.GetRequiredService<ILogger<DailyQuotaManager>>();
            return new DailyQuotaManager(owner, gate, logger);
        });

        services.TryAddScoped<IMemoryService, MemoryService>();

        services.TryAddSingleton<IMemoryExtractor, Core.Evolution.LlmMemoryExtractor>();
        services.TryAddSingleton<IMemoryPipeline, Core.Pipeline.MemoryPipeline>();
        services.TryAddSingleton<ISessionMemoryBuffer, SessionMemoryBuffer>();
        services.TryAddSingleton<IMemoryFlushService, MemoryFlushService>();
        services.TryAddSingleton<ISessionActivityTracker, SessionActivityTracker>();
        services.TryAddSingleton<IMemoryEvolutionService, Core.Evolution.LlmMemoryEvolution>();
        services.TryAddSingleton<IMemoryRecallService, Core.Recall.MemoryRecallService>();

        services.TryAddSingleton<ChatMemoryHandler>();
        services.TryAddSingleton<ToolMemoryHandler>();
        services.TryAddSingleton<AgentTurnMemoryHandler>();
        services.TryAddSingleton<MemoryRecallHandler>();

        services.TryAddSingleton<MemorySearchTool>();
        services.TryAddSingleton<MemoryWriteTool>();
        services.TryAddSingleton<MemoryReadTool>();

        services.AddModuleHostedService<MemoryPipelineWorker>();
        services.AddModuleHostedService<MemoryEvolutionWorker>();
        services.AddModuleHostedService<MemoryIndexingService>();

        return services;
    }

    public static IServiceCollection AddMemoryServices<TEmbedding>(
        this IServiceCollection services,
        IConfigSectionRegistry registry,
        string? connectionString = null)
        where TEmbedding : class, IEmbeddingService
    {
        services.AddMemoryServices(registry, connectionString);
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

    /// <summary>
    /// 解析 SQLite 连接字符串。未显式传入时，默认使用项目级 .seeing 隐藏目录下的 memory.db。
    /// </summary>
    private static string ResolveConnectionString(IServiceProvider sp, string? connectionString)
    {
        if (!string.IsNullOrWhiteSpace(connectionString))
            return connectionString;

        var dirs = sp.GetService<ISeeingDirectories>();
        var dbDir = dirs?.ProjectSeeingDirectory
            // 兜底：无工作区提供者时回退到当前目录的 .seeing
            ?? Path.Combine(Directory.GetCurrentDirectory(), ".seeing");

        Directory.CreateDirectory(dbDir);
        return $"Data Source={Path.Combine(dbDir, "memory.db")}";
    }
}
