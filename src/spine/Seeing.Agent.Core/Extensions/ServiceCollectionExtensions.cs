using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Abstractions.Components;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Seeing.Agent.Core.Commands;
using Seeing.Agent.Core.Commands.Discovery;
using Seeing.Agent.Core.Compression;
using Seeing.Agent.Abstractions.Compression;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Abstractions.Ui;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Core.Instructions;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Core.Permission;
using Seeing.Agent.Abstractions.Prompts;
using Seeing.Agent.Core.Prompts;
using Seeing.Agent.Core.Scheduling;
using Seeing.Agent.Abstractions.Todo;
using Seeing.Agent.Core.Todo;
using Seeing.Agent.Core.Decorators;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Core.Execution;
using Seeing.Agent.Core.Hosting;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Core.Scenarios;
using Seeing.Agent.Core.CapabilitySets;
using Seeing.Agent.Core.Modules;
using Seeing.Agent.Core.Llm;
using Seeing.Agent.Llm;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Summarization;
using System.Net.Http;
using Seeing.Agent.Core.Middlewares;
using Seeing.Agent.Core.Shell;
using Seeing.Agent.Core.Tools;
using Seeing.Agent.Core.Tools.BuiltIn;
using Seeing.Agent.Core.Todo;
using Seeing.Session.Core;
using Seeing.Session.Management;
using Seeing.Session.Storage;

namespace Seeing.Agent.Core.Extensions
{
    /// <summary>
    /// 服务集合扩展 - DI 注册入口
    /// </summary>
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// 注册 Seeing 脊柱核心服务。须在能力包 <see cref="AddSeeingModule{T}"/> / AddSeeing* 薄封装之后调用。
        /// </summary>
        /// <param name="services">服务集合</param>
        /// <param name="registry">宿主共享的配置节注册表（模块与 Core 共用同一实例）</param>
        public static IServiceCollection AddSeeingCore(
            this IServiceCollection services,
            IConfigSectionRegistry registry)
        {
            ArgumentNullException.ThrowIfNull(registry);
            services.EnsureConfigSectionRegistry(registry);
            if (registry is ConfigSectionRegistry concrete)
                concrete.RegisterSpineSections();

            // 注册 UnifiedConfigManager（不在 factory 内同步 Load；由 InitializeSeeingAsync 加载）
            services.AddSingleton<UnifiedConfigManager>(sp =>
            {
                var workspace = sp.GetRequiredService<IWorkspaceProvider>();
                var logger = sp.GetRequiredService<ILogger<UnifiedConfigManager>>();
                var sectionRegistry = sp.GetRequiredService<IConfigSectionRegistry>();
                return new UnifiedConfigManager(workspace, logger, sectionRegistry);
            });
            services.AddSingleton<IConfigSectionStore>(sp => sp.GetRequiredService<UnifiedConfigManager>());

            // IOptions 兼容 + IOptionsMonitor 支持热重载
            services.AddSingleton<SeeingAgentOptionsMonitor>();
            services.AddSingleton<IOptions<SeeingAgentOptions>>(sp => sp.GetRequiredService<SeeingAgentOptionsMonitor>());
            services.AddSingleton<IOptionsMonitor<SeeingAgentOptions>>(sp => sp.GetRequiredService<SeeingAgentOptionsMonitor>());

            RegisterCoreServices(services);

            RegisterLlmServices(services);

            // 统一重载 Handler 注册（依赖各 Manager，须在各 Manager 注册之后）
            RegisterReloadHandlers(services);

            // 配置 LLM 专用的 HttpClient，解决 SSL 连接池陈旧连接问题
            services.AddHttpClient("LlmClient")
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
                {
                    // 连接空闲超时：超过此时间的空闲连接将被关闭
                    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                    // 连接最大存活时间：强制刷新连接，防止服务器端关闭导致的解密失败
                    PooledConnectionLifetime = TimeSpan.FromMinutes(5)
                });

            services.AddHttpClient();

            return services;
        }

        /// <summary>
        /// 注册 Seeing 脊柱核心服务（使用自定义配置）。
        /// </summary>
        public static IServiceCollection AddSeeingCore(
            this IServiceCollection services,
            IConfigSectionRegistry registry,
            Action<SeeingAgentOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(registry);
            ArgumentNullException.ThrowIfNull(configure);
            services.EnsureConfigSectionRegistry(registry);
            if (registry is ConfigSectionRegistry concrete)
                concrete.RegisterSpineSections();

            // 注册 UnifiedConfigManager（不在 factory 内同步 Load）
            services.AddSingleton<UnifiedConfigManager>(sp =>
            {
                var workspace = sp.GetRequiredService<IWorkspaceProvider>();
                var logger = sp.GetRequiredService<ILogger<UnifiedConfigManager>>();
                var sectionRegistry = sp.GetRequiredService<IConfigSectionRegistry>();
                var manager = new UnifiedConfigManager(workspace, logger, sectionRegistry);
                configure(manager.GetSeeingAgentOptions());
                return manager;
            });
            services.AddSingleton<IConfigSectionStore>(sp => sp.GetRequiredService<UnifiedConfigManager>());

            // IOptions 兼容 + IOptionsMonitor 支持热重载
            services.AddSingleton<SeeingAgentOptionsMonitor>();
            services.AddSingleton<IOptions<SeeingAgentOptions>>(sp => sp.GetRequiredService<SeeingAgentOptionsMonitor>());
            services.AddSingleton<IOptionsMonitor<SeeingAgentOptions>>(sp => sp.GetRequiredService<SeeingAgentOptionsMonitor>());

            RegisterCoreServices(services);

            RegisterLlmServices(services);

            // 统一重载 Handler 注册（依赖各 Manager，须在各 Manager 注册之后）
            RegisterReloadHandlers(services);

            // 配置 LLM 专用的 HttpClient，解决 SSL 连接池陈旧连接问题
            services.AddHttpClient("LlmClient")
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
                {
                    // 连接空闲超时：超过此时间的空闲连接将被关闭
                    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                    // 连接最大存活时间：强制刷新连接，防止服务器端关闭导致的解密失败
                    PooledConnectionLifetime = TimeSpan.FromMinutes(5)
                });

            services.AddHttpClient();

            return services;
        }

        /// <summary>
        /// 登记能力模块：调用 <see cref="ISeeingModule.ConfigureServices"/> 并注册为 <see cref="ISeeingModule"/>。
        /// 须在 <see cref="AddSeeingCore"/> 之前调用。
        /// </summary>
        public static IServiceCollection AddSeeingModule<TModule>(
            this IServiceCollection services,
            IConfigSectionRegistry registry)
            where TModule : class, ISeeingModule, new()
        {
            ArgumentNullException.ThrowIfNull(registry);
            services.EnsureConfigSectionRegistry(registry);

            var module = new TModule();
            module.ConfigureServices(services);
            services.AddSingleton<ISeeingModule>(sp =>
            {
                var agentStoreCtor = typeof(TModule).GetConstructor([typeof(IAgentStore)]);
                if (agentStoreCtor is not null)
                {
                    var store = sp.GetService<IAgentStore>();
                    if (store is not null)
                        return (TModule)agentStoreCtor.Invoke([store])!;
                }

                var uiCtor = typeof(TModule).GetConstructor([typeof(IUiContributionRegistry)]);
                if (uiCtor is not null)
                {
                    var ui = sp.GetService<IUiContributionRegistry>();
                    return (TModule)uiCtor.Invoke([ui])!;
                }

                return new TModule();
            });
            return services;
        }

        /// <summary>
        /// 注册执行管道和中间件
        /// </summary>
        public static IServiceCollection AddExecutionPipeline(
            this IServiceCollection services,
            Action<PipelineBuilder>? configure = null)
        {
            // 注册管道
            services.AddSingleton<IExecutionPipeline, ExecutionPipeline>();

            // 注册默认中间件
            services.AddTransient<LoggingMiddleware>();
            services.AddTransient<RetryMiddleware>();

            return services;
        }

        /// <summary>
        /// 注册工具类型（使用注解发现）
        /// </summary>
        public static IServiceCollection AddToolsFromType<T>(
            this IServiceCollection services)
        {
            // 注册类型本身（如果需要 DI）
            services.AddTransient(typeof(T));

            // 工具发现和注册在 ToolManager 中完成
            return services;
        }

        /// <summary>
        /// 注册命令系统
        /// </summary>
        public static IServiceCollection AddCommandSystem(
            this IServiceCollection services,
            Action<CommandSystemOptions>? configure = null)
        {
            // 配置选项
            var options = new CommandSystemOptions();
            configure?.Invoke(options);

            // 注册命令注册表
            services.AddSingleton<ICommandRegistry, CommandRegistry>();

            // 注册命令发现器（具体类 + Abstractions 端口）
            services.AddSingleton<CommandDiscovery>();
            services.AddSingleton<ICommandDiscovery>(sp => sp.GetRequiredService<CommandDiscovery>());

            // 如果配置了自动发现，从指定程序集发现命令
            if (options.DiscoveryAssemblies != null && options.DiscoveryAssemblies.Count > 0)
            {
                services.AddSingleton<ICommandDiscoveryInitializer>(sp =>
                {
                    var registry = sp.GetRequiredService<ICommandRegistry>();
                    var discovery = sp.GetRequiredService<CommandDiscovery>();
                    var logger = sp.GetService<ILogger<ICommandDiscoveryInitializer>>();

                    return new CommandDiscoveryInitializer(
                        registry,
                        discovery,
                        options.DiscoveryAssemblies,
                        sp,
                        logger);
                });
            }

            return services;
        }

        /// <summary>
        /// 注册命令类型（手动方式）
        /// </summary>
        public static IServiceCollection AddCommand<TCommand>(
            this IServiceCollection services)
            where TCommand : class, ICommand
        {
            services.AddSingleton<TCommand>();
            return services;
        }

        /// <summary>
        /// 初始化命令系统（在服务提供者构建后调用）
        /// </summary>
        public static IServiceProvider InitializeCommands(
            this IServiceProvider services,
            IEnumerable<ICommand>? additionalCommands = null)
        {
            var registry = services.GetRequiredService<ICommandRegistry>();

            // 注册手动添加的命令
            if (additionalCommands != null)
            {
                registry.RegisterAll(additionalCommands);
            }

            // 执行自动发现初始化
            var initializer = services.GetService<ICommandDiscoveryInitializer>();
            if (initializer != null)
            {
                initializer.Initialize();
            }

            return services;
        }

        /// <summary>
        /// 注册 LLM 服务
        /// </summary>
        public static IServiceCollection AddLlmProviders(
            this IServiceCollection services,
            IConfiguration? configuration = null)
        {
            _ = configuration;

            if (!services.Any(d => d.ServiceType == typeof(UnifiedConfigManager)))
            {
                services.GetOrCreateConfigSectionRegistry();

                services.AddSingleton<UnifiedConfigManager>(sp =>
                {
                    var workspace = sp.GetRequiredService<IWorkspaceProvider>();
                    var logger = sp.GetRequiredService<ILogger<UnifiedConfigManager>>();
                    var registry = sp.GetRequiredService<IConfigSectionRegistry>();
                    return new UnifiedConfigManager(workspace, logger, registry);
                });
                services.AddSingleton<IConfigSectionStore>(sp => sp.GetRequiredService<UnifiedConfigManager>());
                
            // IOptions 兼容 + IOptionsMonitor 支持热重载
            services.AddSingleton<SeeingAgentOptionsMonitor>();
            services.AddSingleton<IOptions<SeeingAgentOptions>, SeeingAgentOptionsMonitor>();
            services.AddSingleton<IOptionsMonitor<SeeingAgentOptions>, SeeingAgentOptionsMonitor>();
            }
            else if (!services.Any(d => d.ServiceType == typeof(IConfigSectionStore)))
            {
                services.AddSingleton<IConfigSectionStore>(sp => sp.GetRequiredService<UnifiedConfigManager>());
            }

            RegisterLlmServices(services);

            // 配置 LLM 专用的 HttpClient，解决 SSL 连接池陈旧连接问题
            services.AddHttpClient("LlmClient")
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
                {
                    // 连接空闲超时：超过此时间的空闲连接将被关闭
                    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                    // 连接最大存活时间：强制刷新连接，防止服务器端关闭导致的解密失败
                    PooledConnectionLifetime = TimeSpan.FromMinutes(5)
                });

            services.AddHttpClient();

            return services;
        }

        /// <summary>
        /// 注册核心服务
        /// </summary>
        private static void RegisterCoreServices(IServiceCollection services)
        {
            // 进程级模块目录 / 结算 / 生命周期
            // ProcessSettlementOptions 由 Host Shape 登记（HostDefaultBoot/Seams/Scenario）；无宿主则保持未注册。
            services.TryAddSingleton<ModuleCatalog>();
            services.TryAddSingleton<IModuleCatalog>(sp => sp.GetRequiredService<ModuleCatalog>());
            services.TryAddSingleton<SettlementEngine>();
            services.TryAddSingleton<IScenarioCatalog, ScenarioCatalog>();
            services.TryAddSingleton<ICapabilitySetCatalog, CapabilitySetCatalog>();
            services.TryAddSingleton<IDefaultWorkModeProvider, DefaultWorkModeProvider>();
            services.TryAddSingleton<ModuleLifecycleManager>();
            services.TryAddSingleton<ModuleReloadOptions>();

            // Shell 配置节（ShellOptions 在 Abstractions；工具能力包由宿主 AddSeeingModule 登记）
            services.GetOrCreateConfigSectionRegistry().Register(
                new ConfigSectionMeta("Shell", "seeing.json", ConfigScope.Both, typeof(ShellOptions)));
            services.AddOptions<ShellOptions>();
            services.TryAddSingleton(sp =>
                new ConfigSectionOptionsMonitor<ShellOptions>(
                    sp.GetRequiredService<IConfigSectionStore>(), "Shell"));
            services.TryAddSingleton<IOptionsMonitor<ShellOptions>>(sp =>
                sp.GetRequiredService<ConfigSectionOptionsMonitor<ShellOptions>>());
            services.TryAddSingleton<IOptions<ShellOptions>>(sp =>
                sp.GetRequiredService<ConfigSectionOptionsMonitor<ShellOptions>>());

            // 执行事件发布器兜底：Core-only 组合无 Hosting 执行引擎；真实宿主由 AddExecutionEngine
            // 以 AddSingleton 在后注册覆盖（单服务解析取最后一个注册）。
            services.TryAddSingleton<IExecutionEventPublisher, NullExecutionEventPublisher>();

            // 权限服务（新系统 - 统一权限检查入口）
            services.AddPermissionService();

            // Hook 管理器
            services.AddSingleton<HookManager>();
            services.AddSingleton<Seeing.Agent.Abstractions.Hooks.IHookManager>(sp => sp.GetRequiredService<HookManager>());
            // Session Hook 管理器适配器（让 SessionManager 使用）
            services.AddSingleton<Seeing.Session.Hooks.IHookManager>(sp =>
                new Seeing.Agent.Core.Services.HookManagerAdapter(sp.GetRequiredService<HookManager>()));

            // 提示词构建服务
            services.AddPromptBuilder();

            // Agent 发现服务（MD 发现仍用于测试/其他调用方；注册路径不再同步 Discover）
            services.AddSingleton<AgentDiscovery>();

            // Agent 存储（纯存储操作）
            services.AddSingleton<AgentStore>();
            services.AddSingleton<IAgentStore>(sp => sp.GetRequiredService<AgentStore>());

            // Agent 运行时管理器（运行时设置）
            services.AddSingleton<AgentRuntimeManager>();
            services.AddSingleton<IAgentRuntimeManager>(sp => sp.GetRequiredService<AgentRuntimeManager>());

            // Agent 管理器（统一管理注册、发现、配置）
            // 两阶段：构造时 store 为空；内置人格由宿主 AddSeeingModule<AgentsBuiltInModule> → ActivateAsync 写入
            services.AddSingleton<AgentManager>(sp =>
            {
                var logger = sp.GetRequiredService<ILogger<AgentManager>>();
                var agentStore = sp.GetRequiredService<IAgentStore>();
                var runtimeManager = sp.GetRequiredService<IAgentRuntimeManager>();
                var workspaceProvider = sp.GetRequiredService<IWorkspaceProvider>();
                var options = sp.GetService<IOptions<SeeingAgentOptions>>();

                return new AgentManager(
                    logger,
                    agentStore,
                    runtimeManager,
                    workspaceProvider,
                    defaultAgent: options?.Value?.DefaultAgent,
                    options: options);
            });
            // 兼容旧接口
            services.AddSingleton<IAgentRegistry>(sp => sp.GetRequiredService<AgentManager>());

            // AgentManager 自身实现 IHostedService，启动时自动加载 MD 配置
            services.AddHostedService<AgentManager>(sp => sp.GetRequiredService<AgentManager>());

            // Agent 运行时初始化服务（IHostedService）
            services.AddHostedService<AgentInitializationService>();

            // 会话持久化写回配置（共享单例；宿主可预先注册实例以调整，默认启用写回）
            // 以具体实例注册，使下方基于 ImplementationInstance 的 Enabled 探测真正生效。
            // 如需旁路写回：在调用 AddSeeingCore 之前注册
            // new Seeing.Session.Persistence.SessionPersistenceOptions { Enabled = false } 实例。
            services.TryAddSingleton(new Seeing.Session.Persistence.SessionPersistenceOptions());

            // 写回开关：宿主在注册前提供 SessionPersistenceOptions 实例并设置 Enabled=false 可旁路装饰器
            var writeBehindEnabled = (services
                .LastOrDefault(d => d.ServiceType == typeof(Seeing.Session.Persistence.SessionPersistenceOptions))
                ?.ImplementationInstance as Seeing.Session.Persistence.SessionPersistenceOptions)?.Enabled ?? true;

            // 会话管理器 - 必须注入 ISessionStore，否则 SaveAsync 只会警告「未配置 SessionStore」
            if (!services.Any(d => d.ServiceType == typeof(ISessionStore)))
            {
                if (writeBehindEnabled)
                {
                    // 具体装饰器单例 + 接口映射到同一实例：刷新状态共享；容器对具体单例仅释放一次，
                    // 调度器 Dispose 内部 Interlocked 幂等，重复捕获亦安全
                    services.AddSingleton<Seeing.Session.Persistence.WriteBehindSessionStore>(sp =>
                    {
                        var workspace = sp.GetRequiredService<IWorkspaceProvider>();
                        var path = Path.Combine(workspace.ProjectSeeingDirectory, "sessions");
                        var inner = new FileSessionStore(path, sp.GetService<ILogger<FileSessionStore>>());
                        var options = sp.GetRequiredService<Seeing.Session.Persistence.SessionPersistenceOptions>();
                        return new Seeing.Session.Persistence.WriteBehindSessionStore(
                            inner,
                            options,
                            sp.GetService<ILogger<Seeing.Session.Persistence.WriteBehindSessionStore>>());
                    });
                    services.AddSingleton<ISessionStore>(sp =>
                        sp.GetRequiredService<Seeing.Session.Persistence.WriteBehindSessionStore>());
                    services.AddSingleton<Seeing.Session.Storage.IWriteBehindSessionStore>(sp =>
                        sp.GetRequiredService<Seeing.Session.Persistence.WriteBehindSessionStore>());
                }
                else
                {
                    services.AddSingleton<ISessionStore>(sp =>
                    {
                        var workspace = sp.GetRequiredService<IWorkspaceProvider>();
                        var path = Path.Combine(workspace.ProjectSeeingDirectory, "sessions");
                        return new FileSessionStore(
                            path,
                            sp.GetService<ILogger<FileSessionStore>>());
                    });
                }
            }

            // 会话事件发布器（SessionManager / UI 必须共用同一实例）
            services.TryAddSingleton<ISessionEventPublisher, SessionEventPublisher>();

            // SessionManager 必须在 ISessionEventPublisher 注册之后，工厂解析时才能注入非 null publisher
            services.AddSingleton<SessionManager>(sp =>
                new SessionManager(
                    store: sp.GetRequiredService<ISessionStore>(),
                    hookManager: sp.GetService<Seeing.Session.Hooks.IHookManager>(),
                    eventPublisher: sp.GetRequiredService<ISessionEventPublisher>(),
                    logger: sp.GetService<ILogger<SessionManager>>(),
                    catalog: sp.GetService<ISessionCatalog>()));
            services.AddSingleton<ISessionManager>(sp =>
                sp.GetRequiredService<SessionManager>());

            // 会话组存储（关系唯一权威在 SessionGroup）：与会话存储同根目录下 session-groups
            if (!services.Any(d => d.ServiceType == typeof(ISessionGroupStore)))
            {
                if (writeBehindEnabled)
                {
                    // 具体装饰器单例 + 接口映射到同一实例：刷新状态共享；容器对具体单例仅释放一次，
                    // 调度器 Dispose 内部 Interlocked 幂等，重复捕获亦安全
                    services.AddSingleton<Seeing.Session.Persistence.WriteBehindSessionGroupStore>(sp =>
                    {
                        var workspace = sp.GetRequiredService<IWorkspaceProvider>();
                        var path = Path.Combine(workspace.ProjectSeeingDirectory, "session-groups");
                        var inner = new FileSessionGroupStore(path, sp.GetService<ILogger<FileSessionGroupStore>>());
                        var options = sp.GetRequiredService<Seeing.Session.Persistence.SessionPersistenceOptions>();
                        return new Seeing.Session.Persistence.WriteBehindSessionGroupStore(
                            inner,
                            options,
                            sp.GetService<ILogger<Seeing.Session.Persistence.WriteBehindSessionGroupStore>>());
                    });
                    services.AddSingleton<ISessionGroupStore>(sp =>
                        sp.GetRequiredService<Seeing.Session.Persistence.WriteBehindSessionGroupStore>());
                    services.AddSingleton<Seeing.Session.Persistence.IWriteBehindSessionGroupStore>(sp =>
                        sp.GetRequiredService<Seeing.Session.Persistence.WriteBehindSessionGroupStore>());
                }
                else
                {
                    services.AddSingleton<ISessionGroupStore>(sp =>
                    {
                        var workspace = sp.GetRequiredService<IWorkspaceProvider>();
                        var path = Path.Combine(workspace.ProjectSeeingDirectory, "session-groups");
                        return new FileSessionGroupStore(
                            path,
                            sp.GetService<ILogger<FileSessionGroupStore>>());
                    });
                }
            }

            // 会话分支器（纯消息复制引擎；不承载关系）
            services.TryAddSingleton(sp =>
                new SessionForker(
                    sp.GetService<ILogger<SessionForker>>() ?? NullLogger<SessionForker>.Instance,
                    sp.GetRequiredService<ISessionManager>()));

            // 会话组管理器：关系（父子 / 分支 / 交接）的唯一权威
            services.TryAddSingleton(sp =>
                new SessionGroupManager(
                    sp.GetRequiredService<ISessionManager>(),
                    sp.GetRequiredService<ISessionGroupStore>(),
                    sp.GetRequiredService<SessionForker>(),
                    sp.GetService<ILogger<SessionGroupManager>>()));
            services.TryAddSingleton<ISessionGroupManager>(sp =>
                sp.GetRequiredService<SessionGroupManager>());

            // 会话组事件总线 + 管理器→总线桥接（UI 订阅组变更快照）
            services.AddSingleton<ISessionGroupEventBus, ChannelSessionGroupEventBus>();
            services.AddHostedService<SessionGroupEventBridge>();

            // 工具输出落盘服务（超限工具输出写会话 ref 目录）
            services.AddSingleton<Seeing.Agent.Core.Output.IToolOutputStore>(sp =>
                new Seeing.Agent.Core.Output.SessionToolOutputStore(
                    sp.GetRequiredService<ISessionStore>(),
                    sp.GetService<ILogger<Seeing.Agent.Core.Output.SessionToolOutputStore>>()));

            // 压缩组件（主库唯一注册点，TokenBudget 扩展不重复注册）
            services.AddSingleton<ISummarizer, LlmSummarizer>();
            services.AddSingleton<CompressionService>();
            services.AddSingleton<ICompressionService>(sp => sp.GetRequiredService<CompressionService>());
            services.AddSingleton<CompressionOptions>();

            // 能力包工具 / Skills / MCP / LLM / Agents.BuiltIn — 由宿主 AddSeeingModule<T> 登记

            // ========== 注册装饰器链（重试→超时→缓存）==========
            // 超时由 ToolTimeoutDecorator 在工具执行漏斗内施加：读取工具能力声明
            // （timeout.skip=true 豁免、timeout.budget 指定自身上限），未声明时回落到
            // SeeingAgentOptions.ToolExecutionTimeout 全局兜底（IOptionsMonitor 实时读取）。
            // 长耗时工具（如 TaskTool 子代理）经 timeout.skip=true 豁免。缓存仅对声明
            // cache.enabled=true 的外部工具生效（见 ToolCapabilityKeys），内置工具均不缓存。
            services.AddSingleton<IToolDecoratorRegistry>(sp =>
            {
                var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
                var cache = sp.GetService<IMemoryCache>();

                var registry = new ToolDecoratorRegistry(sp);

                // 最外层：重试装饰器（3 次，1 秒间隔，指数退避）
                registry.Register(tool => new RetryToolDecorator(
                    tool,
                    maxRetries: 3,
                    delay: TimeSpan.FromSeconds(1),
                    logger: loggerFactory.CreateLogger<RetryToolDecorator>()));

                // 中间层：超时装饰器（能力感知，调用时解析 timeout.skip/budget，兜底全局 ToolExecutionTimeout）
                registry.Register(tool => new ToolTimeoutDecorator(
                    tool,
                    sp.GetRequiredService<IOptionsMonitor<SeeingAgentOptions>>(),
                    loggerFactory.CreateLogger<ToolTimeoutDecorator>()));

                // 最内层：缓存装饰器（5 分钟过期）— 仅在 IMemoryCache 可用时
                if (cache != null)
                {
                    registry.Register(tool => new CachedToolDecorator(
                        tool,
                        cache,
                        TimeSpan.FromMinutes(5),
                        loggerFactory.CreateLogger<CachedToolDecorator>()));
                }

                // 最外层兜底：输出限制装饰器（后注册=最外层，处理最终返回给调用方的结果）
                registry.Register(tool => new ToolOutputLimiterDecorator(
                    tool,
                    sp.GetRequiredService<IOptionsMonitor<SeeingAgentOptions>>(),
                    sp.GetRequiredService<Seeing.Agent.Core.Output.IToolOutputStore>(),
                    sp.GetRequiredService<IPermissionGrantStore>(),
                    loggerFactory.CreateLogger<ToolOutputLimiterDecorator>()));

                return registry;
            });

            // 工具权限策略
            services.TryAddSingleton<IToolPermissionPolicy, DefaultToolPermissionPolicy>();

            // 工具调用器（空注册表；工具由各模块 Activate → IToolManager.RegisterTool 挂载）
            services.AddSingleton<ToolManager>(sp =>
            {
                var logger = sp.GetRequiredService<ILogger<ToolManager>>();
                var hookManager = sp.GetRequiredService<Seeing.Agent.Abstractions.Hooks.IHookManager>();
                var decoratorRegistry = sp.GetService<IToolDecoratorRegistry>();
                var workspace = sp.GetService<IWorkspaceProvider>();
                var permissionPolicy = sp.GetService<IToolPermissionPolicy>();

                return new ToolManager(logger, hookManager, sp, decoratorRegistry,
                    workspace: workspace, permissionPolicy: permissionPolicy);
            });
            services.AddSingleton<IToolManager>(sp => sp.GetRequiredService<ToolManager>());

            // 5. 工作区路径提供者（统一管理配置目录）
            // 初始化时根据配置自动解析工作区
            services.AddSingleton<WorkspaceProvider>();
            services.AddSingleton<IWorkspaceProvider>(sp => sp.GetRequiredService<WorkspaceProvider>());
            services.AddSingleton<ISeeingDirectories>(sp => sp.GetRequiredService<IWorkspaceProvider>());

            // 5.1 统一重载编排器（订阅配置/工作区变更，调度所有 IReloadHandler）
            services.AddSingleton<ReloadOrchestrator>();
            // 插件推送与动态注册入口：均指向编排器，扩展包只引用 Abstractions 接口
            services.AddSingleton<IReloadSignalBus>(sp => sp.GetRequiredService<ReloadOrchestrator>());
            services.AddSingleton<IReloadHandlerRegistry>(sp => sp.GetRequiredService<ReloadOrchestrator>());
            // 宿主启动时构造编排器（惰性单例需显式解析，否则不订阅变更事件）
            services.AddHostedService<ReloadOrchestratorStarter>();

            // 5.2 Todo 存储（端口-适配器：基于 Session Context 的默认实现）
            services.AddSingleton<ITodoStore, SessionContextTodoStore>();

            // 6. Agent / Model 默认解析 + 系统提醒渲染
            services.AddSingleton<AgentSelectionResolver>();
            services.AddSingleton<Seeing.Agent.Abstractions.Agents.IAgentSelectionResolver>(
                sp => sp.GetRequiredService<AgentSelectionResolver>());
            services.AddSingleton<Seeing.Agent.Abstractions.Reminders.ISystemReminderRenderer,
                Seeing.Agent.Core.Reminders.SystemReminderRendererService>();

            // 执行上下文相关
            services.AddSingleton<IMetadataStore, ConcurrentMetadataStore>();

            // 工作区路径硬边界门闸（白名单/记忆经 IPermissionGrantStore 单一存储）
            services.AddSingleton<IWorkspacePathGate, WorkspacePathGate>();
            services.AddSingleton<WorkspaceBoundaryLifecycle>();

            // Agent 执行器（统一执行引擎）
            services.AddSingleton<AgentExecutor>();

            // Native 实现 + Router 门面（ACP 等包追加 IAgentExecutorImplementation，不替换 IAgentExecutor）
            services.AddSingleton<IAgentExecutorImplementation, NativeAgentExecutor>();
            services.AddSingleton<IAgentExecutor, AgentExecutorRouter>();

            services.AddSingleton<
                Seeing.Agent.Core.Services.ISessionTitleService,
                Seeing.Agent.Core.Services.SessionTitleService>();

            // Shell 环境服务（触发 shell.env Hook）
            services.AddSingleton<IShellEnvironmentService, ShellEnvironmentService>();

            // 命令执行服务（触发 command.execute.before Hook）
            services.AddSingleton<ICommandService, CommandService>();

            services.AddSingleton<IAgentLoopScheduler, AgentLoopScheduler>();

            // 命令注册表（插件加载与扩展命令注册需要）
            services.AddSingleton<ICommandRegistry, CommandRegistry>();

            // 组件管理器（统一管理 Skills/MCP）
            // 同时注册具体类，供 ReloadHandler 注入；接口复用同一实例
            services.AddSingleton<ComponentManager>();
            services.AddSingleton<IComponentManager>(sp => sp.GetRequiredService<ComponentManager>());
        }

        /// <summary>
        /// 注册 LLM 服务
        /// </summary>
        private static void RegisterLlmServices(IServiceCollection services)
        {
            // ILlmClientFactory 由 OpenAiLlmModule / AnthropicLlmModule 注册；
            // ProviderManager 聚合 IEnumerable<ILlmClientFactory>。

            // ModelManager 依赖 IAgentStore（非 IAgentRegistry），避免环：
            // ModelManager → AgentManager → AgentRuntimeManager → IModelManager
            services.AddSingleton<ModelConfigManager>();
            services.AddSingleton<ModelManager>(sp => new ModelManager(
                sp.GetRequiredService<ModelConfigManager>(),
                sp.GetRequiredService<IAgentStore>()));
            services.AddSingleton<IModelManager>(sp => sp.GetRequiredService<ModelManager>());
            services.AddSingleton<IModelConfigManager>(sp => sp.GetRequiredService<ModelManager>());

            // 能力模块可 Replace 为真实现；未加载时 enrich 恒 no-op
            services.TryAddSingleton<IModelCapabilityManager, NullModelCapabilityManager>();
            // 显式 Lazy：打断 ProviderManager ↔ MCM 构造环；MS.DI 不自动提供 Lazy<T>
            services.AddSingleton(sp => new Lazy<IModelCapabilityManager>(
                () => sp.GetRequiredService<IModelCapabilityManager>(),
                LazyThreadSafetyMode.ExecutionAndPublication));

            // Provider 管理器（同时注册具体类，供 ProviderReloadHandler 等注入）
            services.AddSingleton<IProviderRegistry, ProviderRegistry>();
            services.AddSingleton<ILlmCallInterceptorRegistry, LlmCallInterceptorRegistry>();
            services.AddSingleton<ILlmClientDecorator, RetryLlmClientDecorator>();
            services.AddSingleton<ILlmClientDecoratorRegistry>(sp =>
            {
                var registry = new LlmClientDecoratorRegistry();
                foreach (var decorator in sp.GetServices<ILlmClientDecorator>())
                    registry.Register(decorator);
                return registry;
            });
            services.AddSingleton<ProviderManager>();
            services.AddSingleton<IProviderManager>(sp => sp.GetRequiredService<ProviderManager>());

            // LLM 服务（调用层）
            services.AddSingleton<ILlmService, LlmService>();
            services.AddSingleton<IModelConfigLookup>(sp => sp.GetRequiredService<ILlmService>());
            services.AddSingleton<ITextCompletion, TextCompletionService>();
            services.AddSingleton<IProviderEndpointLookup, OptionsProviderEndpointLookup>();
        }

        /// <summary>
        /// 注册统一重载 Handler（组件迁移自 ConfigChanged 自订阅）
        /// </summary>
        private static void RegisterReloadHandlers(IServiceCollection services)
        {
            // 统一重载 Handler 注册（组件迁移自 ConfigChanged 自订阅）
            // 注意：依赖各 Manager，必须在 RegisterCoreServices + RegisterLlmServices 之后调用
            services.AddSingleton<IReloadHandler, ProviderReloadHandler>();
            services.AddSingleton<IReloadHandler, ModelReloadHandler>();
            services.AddSingleton<IReloadHandler, ModelCapabilityCatalogReloadHandler>();
            services.AddSingleton<IReloadHandler, AgentRuntimeReloadHandler>();
            services.AddSingleton<IReloadHandler, AgentManagerReloadHandler>();
            services.AddSingleton<IReloadHandler, SessionReloadHandler>();
            services.AddSingleton<IReloadHandler, SessionGroupReloadHandler>();
            services.AddSingleton<IReloadHandler>(sp => sp.GetRequiredService<ComponentManager>());
            // 进程级模块结算热重载（在途边界；IExecutionInFlightBoundary 由 Hosting 可选登记）
            services.AddSingleton<ModuleSettlementReloadHandler>(sp =>
                new ModuleSettlementReloadHandler(
                    engine: sp.GetRequiredService<SettlementEngine>(),
                    lifecycle: sp.GetRequiredService<ModuleLifecycleManager>(),
                    catalog: sp.GetRequiredService<ModuleCatalog>(),
                    options: sp.GetRequiredService<IOptionsMonitor<SeeingAgentOptions>>(),
                    modules: sp.GetServices<ISeeingModule>(),
                    capabilitySetCatalog: sp.GetService<ICapabilitySetCatalog>(),
                    reloadOptions: sp.GetRequiredService<ModuleReloadOptions>(),
                    settlementOptions: sp.GetService<ProcessSettlementOptions>(),
                    inFlight: sp.GetService<IExecutionInFlightBoundary>(),
                    logger: sp.GetService<ILogger<ModuleSettlementReloadHandler>>()));
            services.AddSingleton<IReloadHandler>(sp =>
                sp.GetRequiredService<ModuleSettlementReloadHandler>());
        }

        /// <summary>
        /// 添加提示词构建服务
        /// </summary>
        public static IServiceCollection AddPromptBuilder(this IServiceCollection services)
        {
            services.AddSingleton<IInstructionManager, InstructionManager>();
            services.AddSingleton<IPromptSectionContributor, ToolsPromptSectionContributor>();
            services.AddSingleton<IPromptSectionContributor, AgentsPromptSectionContributor>();
            services.AddSingleton<IPromptSectionContributor, EnvironmentPromptSectionContributor>();
            services.AddSingleton<PromptBuilder>();

            return services;
        }
    }
}

namespace Seeing.Agent.Core.Extensions
{
    /// <summary>
    /// 初始化扩展 - 使用统一组件管理器加载所有组件
    /// </summary>
    public static class SeeingAgentInitializationExtensions
    {
        /// <summary>
        /// 初始化 Seeing — 工作区 → LoadAsync → 进程级结算 → 模块 Activate → ComponentManager 加载。
        /// 必须在 <c>Host.StartAsync</c> 之前显式调用。
        /// </summary>
        /// <param name="services">服务提供者</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>各组件加载结果</returns>
        public static async Task<IReadOnlyList<ComponentLoadResult>> InitializeSeeingAsync(
            this IServiceProvider services,
            CancellationToken cancellationToken = default)
        {
            var loggerFactory = services.GetService<ILoggerFactory>();
            var logger = loggerFactory?.CreateLogger(typeof(SeeingAgentInitializationExtensions));

            UnifiedConfigManager? configManager = null;

            // 初始化工作区（自动根据配置解析）
            if (services.GetService<WorkspaceProvider>() is { } workspaceProvider)
            {
                configManager = services.GetService<UnifiedConfigManager>();
                var workspaceLogger = services.GetService<ILogger<WorkspaceProvider>>();

                if (configManager != null)
                {
                    // 配置必须先于工作区解析加载（factory 不再同步 Load）
                    await configManager.LoadAsync(cancellationToken);
                    workspaceProvider.SetDependencies(configManager, workspaceLogger);
                    await workspaceProvider.InitializeAsync(cancellationToken);

                    // 工作区可能解析到全局默认/项目自定义路径，项目级配置随之切换；
                    // 需在切换后重新加载，否则启动目录的项目级配置会覆盖全局工作区配置
                    // （如 Permission.AutoApproveAll 读不到全局工作区 setting）。
                    await configManager.LoadAsync(cancellationToken);
                }
            }
            else if (services.GetService<UnifiedConfigManager>() is { } configOnly)
            {
                configManager = configOnly;
                await configOnly.LoadAsync(cancellationToken);
            }

            // 切根时清空白名单与权限记忆（须在工作区 Initialize 之后挂接，避免启动期误清）
            services.GetService<WorkspaceBoundaryLifecycle>()?.Attach();

            // 重载 Handler 延后挂载：须在模块 Activate（可能 Publish）之前，且不得在编排器 ctor 全量物化
            services.AttachReloadHandlers();

            // 进程级结算 → 仅 Activate 启用集（含 agents.builtin → IAgentStore；须在 AgentManager.StartAsync 之前）
            await SettleAndActivateModulesAsync(services, configManager, logger, cancellationToken);

            var componentManager = services.GetRequiredService<IComponentManager>();
            var workspaceRoot = services.GetRequiredService<IWorkspaceProvider>().GetProjectRoot();

            var results = await componentManager.LoadAllAsync(workspaceRoot, cancellationToken);

            // 加载工具禁用状态（技能状态由 SkillLoader 在模块登记时处理）
            var toolInvoker = services.GetService<ToolManager>();
            if (toolInvoker != null)
            {
                await toolInvoker.LoadToolStateAsync(cancellationToken);
            }

            return results;
        }

        /// <summary>
        /// 进程级结算后 Activate 启用集。无 <see cref="SettlementEngine"/> 时回退为激活全部模块（兼容极简宿主）。
        /// </summary>
        private static async Task SettleAndActivateModulesAsync(
            IServiceProvider services,
            UnifiedConfigManager? configManager,
            ILogger? logger,
            CancellationToken cancellationToken)
        {
            var engine = services.GetService<SettlementEngine>();
            var lifecycle = services.GetService<ModuleLifecycleManager>();
            var modules = services.GetServices<ISeeingModule>().ToList();

            if (engine is null || lifecycle is null)
            {
                logger?.LogWarning(
                    "SettlementEngine/ModuleLifecycleManager 未注册，回退为激活全部 {Count} 个 ISeeingModule",
                    modules.Count);
                foreach (var module in modules)
                    await module.ActivateAsync(services, cancellationToken).ConfigureAwait(false);
                return;
            }

            var settlementOptions = services.GetService<ProcessSettlementOptions>();
            var seeing = configManager?.GetSeeingAgentOptions();
            var modulesOptions = seeing?.Modules ?? new ModulesOptions();
            var capabilitySetCatalog = services.GetService<ICapabilitySetCatalog>();

            var input = ModuleSettlementReloadHandler.BuildSettlementInput(
                modules: modules,
                seeing: seeing,
                modulesOptions: modulesOptions,
                settlementOptions: settlementOptions,
                capabilitySetCatalog: capabilitySetCatalog);

            var result = await engine.SettleAsync(input, cancellationToken).ConfigureAwait(false);
            logger?.LogInformation(
                "进程级结算完成：boot={Boot}, scenario={Scenario}, enabled={EnabledCount}, warnings={WarningCount}",
                result.Boot,
                result.Scenario,
                result.Enabled.Count,
                result.Warnings.Count);

            await lifecycle.ActivateAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// 注册自定义组件加载器
        /// </summary>
        public static void RegisterComponentLoader(
            this IServiceProvider services,
            IComponentLoader loader)
        {
            var componentManager = services.GetRequiredService<IComponentManager>();
            componentManager.RegisterLoader(loader);
        }
    }
}
