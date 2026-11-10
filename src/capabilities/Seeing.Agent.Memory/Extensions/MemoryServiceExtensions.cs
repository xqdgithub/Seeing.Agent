using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Context;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Modules;
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
            sp.GetRequiredService<SqliteConnectionOwner>()));

        services.TryAddSingleton<IFileStore, Core.Storage.LocalFileStore>();

        services.TryAddSingleton<IEmbeddingStatus, ConfigurableEmbeddingStatus>();

        // DI 期不再二选一（旧的「按 IsEmbeddingConfigured 决定注册真实现还是 NullEmbeddingService」已废除）：
        // 始终注册 RouterEmbeddingService，未启用/解析失败在调用期抛明确异常。
        // 是否建向量索引由 IEmbeddingStatus 决定，与这里的注册无关。
        services.TryAddSingleton<RouterEmbeddingService>();
        services.TryAddSingleton<IEmbeddingService>(sp =>
        {
            var inner = sp.GetRequiredService<RouterEmbeddingService>();
            var cache = sp.GetService<IEmbeddingCache>();
            var logger = sp.GetService<ILogger<EmbeddingService>>();
            return new EmbeddingService(inner, cache, logger);
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

        // 注：Token 用量统计不在此处记账。唯一权威来源是执行终态快照
        // Seeing.Agent.Abstractions.Execution.ExecutionOutcome.Usage（由 ExecutionRunner 从模型循环累计）。
        // 曾经的 ITokenTracker / IQuotaManager 账本零生产消费者，已删除。
        services.TryAddScoped<IMemoryService, MemoryService>();

        services.TryAddSingleton<IMemoryExtractor, Core.Evolution.LlmMemoryExtractor>();
        services.TryAddSingleton<IMemoryPipeline, Core.Pipeline.MemoryPipeline>();
        services.TryAddSingleton<ISessionMemoryBuffer, SessionMemoryBuffer>();
        services.TryAddSingleton<IMemoryFlushService, MemoryFlushService>();
        // ISessionActivityTracker 已在 Seeing.Session（AddSessionManager / AddSeeingCore）注册，
        // 本包不再重复登记——避免内存与复盘包各持一份导致空闲判定分裂。
        services.TryAddSingleton<IMemoryEvolutionService, Core.Evolution.LlmMemoryEvolution>();
        services.TryAddSingleton<IMemoryRecallService, Core.Recall.MemoryRecallService>();

        // 记忆召回上下文源：读执行作用域的本轮用户查询（Source 只依赖 IContextExecutionScope，不读会话）。
        // 作用域与装配器同作用域（Core 的 AddContextPipeline 以 scoped 注册），故源必须同为 scoped——
        // 单例会捕获某次执行的作用域（钉住静默失效）。
        // 登记必须用 TryAddEnumerable：TryAddScoped<IContextSource, T>() 按 ServiceType 去重，
        // 在已有 ProjectInstructionsContextSource / SkillsContextSource 时会整条静默跳过。
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IContextSource, MemoryContextSource>());

        services.TryAddSingleton<ChatMemoryHandler>(); services.TryAddSingleton<ToolMemoryHandler>();

        // 终态观察者登记必须同时满足两点：①「具体类型 + 接口」解析到同一实例（状态不分裂）；
        // ②重复调用 AddMemoryServices 不翻倍（翻倍会让 TryFlushAfterTurn 调两次，
        // 轮次计数与 flush 时机一起减半）。
        // TryAddEnumerable 不接受工厂描述符：它用 GetImplementationType 判重，而工厂描述符的
        // ImplementationType 为 null，内部回落到「工厂委托的泛型第二实参」即服务类型本身
        // （Func<IServiceProvider, IExecutionOutcomeObserver> 的第二实参就是 IExecutionOutcomeObserver），
        // 于是与服务类型相同 → 判定 "indistinguishable" 并抛 ArgumentException。
        // 故改由「枚举槽用真实实现类型登记、具体类型从枚举槽取回」达成：枚举槽由 TryAddEnumerable
        // 按实现类型去重，具体类型由 TryAddSingleton 按服务类型去重，两条都幂等，
        // 且解析任一入口都落到枚举槽里那唯一一个单例。
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IExecutionOutcomeObserver, AgentTurnMemoryHandler>());
        // 前提（当前不可达，但改动前必读）：具体类型必须由本方法从上面的枚举槽取回。
        // 宿主若自行 AddSingleton<AgentTurnMemoryHandler>() 而不走本方法，接口与具体类型
        // 会各自构造一个实例（状态分裂：轮次计数落在接口实例上，调用方读到的是另一个）。
        services.TryAddSingleton(sp =>
            sp.GetServices<IExecutionOutcomeObserver>().OfType<AgentTurnMemoryHandler>().First());

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
    /// 解析 SQLite 连接字符串。未显式传入时放在用户级 <c>~/.seeing/memory/memory.db</c>。
    /// <para>
    /// 与 Markdown 记忆文件同目录（见 <see cref="Core.Storage.LocalFileStore.GetDefaultMemoryDirectory"/>）：
    /// 索引是这些文件的派生数据，放一起才能整体备份 / 迁移 / 清理。
    /// </para>
    /// <para>
    /// 记忆文件本身固定在用户目录、不随工作区变化，索引也必须跟随它——否则换个工作区就有
    /// 一份空索引，看起来像「记忆全丢了」。旧版把 memory.db 丢在 <c>&lt;工作区&gt;/.seeing/</c> 根下，
    /// 既与配置文件混在一起，又和工作区绑定。
    /// </para>
    /// </summary>
    private static string ResolveConnectionString(IServiceProvider sp, string? connectionString)
    {
        if (!string.IsNullOrWhiteSpace(connectionString))
            return connectionString;

        // 目录约定单点：直接复用文件存储的那条路径，避免两处各写一遍。
        var dbDir = Core.Storage.LocalFileStore.GetDefaultMemoryDirectory();

        Directory.CreateDirectory(dbDir);
        return $"Data Source={Path.Combine(dbDir, "memory.db")}";
    }
}
