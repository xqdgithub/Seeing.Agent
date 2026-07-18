using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Core.Interfaces;
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
        string connectionString = "Data Source=memory.db")
    {
        services.TryAddSingleton<MemoryOptionsProvider>();
        services.TryAddSingleton<IMemoryOptionsStore>(sp => sp.GetRequiredService<MemoryOptionsProvider>());
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

        services.TryAddSingleton(sp =>
        {
            var connection = new SqliteConnection(connectionString);
            connection.Open();
            return connection;
        });
        services.TryAddSingleton<SqliteConnectionGate>();

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
            var connection = sp.GetRequiredService<SqliteConnection>();
            var gate = sp.GetRequiredService<SqliteConnectionGate>();
            var embeddingService = sp.GetRequiredService<IEmbeddingService>();
            var logger = sp.GetRequiredService<ILogger<VectorIndex>>();
            return new VectorIndex(connection, gate, embeddingService, logger);
        });

        services.TryAddSingleton<IKeywordIndex>(sp =>
        {
            var connection = sp.GetRequiredService<SqliteConnection>();
            var gate = sp.GetRequiredService<SqliteConnectionGate>();
            var logger = sp.GetRequiredService<ILogger<KeywordIndex>>();
            return new KeywordIndex(connection, gate, logger);
        });

        services.TryAddSingleton<IMemoryIndex, HybridMemoryIndex>();

        services.TryAddSingleton<IMemoryGraph>(sp =>
        {
            var connection = sp.GetRequiredService<SqliteConnection>();
            var gate = sp.GetRequiredService<SqliteConnectionGate>();
            var logger = sp.GetRequiredService<ILogger<SqliteMemoryGraph>>();
            return new SqliteMemoryGraph(connection, gate, logger);
        });

        services.TryAddSingleton<IEmbeddingCache>(sp =>
        {
            var connection = sp.GetRequiredService<SqliteConnection>();
            var gate = sp.GetRequiredService<SqliteConnectionGate>();
            var logger = sp.GetRequiredService<ILogger<SqliteEmbeddingCache>>();
            return new SqliteEmbeddingCache(connection, gate, logger);
        });

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

        services.TryAddScoped<IMemoryService, MemoryService>();

        services.TryAddSingleton<IMemoryExtractor, Core.Evolution.LlmMemoryExtractor>();
        services.TryAddSingleton<IMemoryPipeline, Core.Pipeline.MemoryPipeline>();
        services.TryAddSingleton<ISessionActivityTracker, SessionActivityTracker>();
        services.TryAddSingleton<IMemoryEvolutionService, Core.Evolution.LlmMemoryEvolution>();
        services.TryAddSingleton<IMemoryRecallService, Core.Recall.MemoryRecallService>();

        services.TryAddSingleton<ChatMemoryHandler>();
        services.TryAddSingleton<ToolMemoryHandler>();
        services.TryAddSingleton<MemoryRecallHandler>();

        services.TryAddSingleton<MemorySearchTool>();
        services.TryAddSingleton<MemoryWriteTool>();
        services.TryAddSingleton<MemoryReadTool>();
        // 注意：不能用 TryAddSingleton<ITool> —— AddSeeingAgent 已注册多个 ITool，
        // TryAdd 会因 ServiceType 已存在而整条跳过，导致 memory 工具从未进入 ToolInvoker。
        services.AddSingleton<ITool>(sp => sp.GetRequiredService<MemorySearchTool>());
        services.AddSingleton<ITool>(sp => sp.GetRequiredService<MemoryWriteTool>());
        services.AddSingleton<ITool>(sp => sp.GetRequiredService<MemoryReadTool>());

        services.AddHostedService<MemoryPipelineWorker>();
        services.AddHostedService<MemoryEvolutionWorker>();
        services.AddHostedService<MemoryIndexingService>();
        services.AddHostedService<MemoryBootstrapHostedService>();

        return services;
    }

    public static IServiceCollection AddMemoryServices<TEmbedding>(
        this IServiceCollection services,
        string connectionString = "Data Source=memory.db")
        where TEmbedding : class, IEmbeddingService
    {
        services.AddMemoryServices(connectionString);
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
