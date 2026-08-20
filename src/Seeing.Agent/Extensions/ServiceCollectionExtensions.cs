using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Abstractions.Components;
using Seeing.Agent.Abstractions.Skills;
using Seeing.Agent.Abstractions.Extensions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using Seeing.Agent.Commands;
using Seeing.Agent.Commands.Discovery;
using Seeing.Agent.Compression;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Background;
using Seeing.Agent.Core.BuiltInAgents;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Core.Instructions;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Core.Permission;
using Seeing.Agent.Core.Prompts;
using Seeing.Agent.Core.Scheduling;
using Seeing.Agent.Abstractions.Todo;
using Seeing.Agent.Core.Events;
using Seeing.Agent.Core.Todo;
using Seeing.Agent.Decorators;
using Seeing.Agent.Llm;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Llm.Clients;
using Seeing.Agent.MCP;
using Seeing.Agent.MCP.Configuration;
using Seeing.Agent.Abstractions.Mcp;
using Seeing.Agent.Abstractions.Summarization;
using Seeing.Agent.MCP.Factory;
using Seeing.Agent.MCP.Management;
using Seeing.Agent.Skills.OnlineParsers;
using System.Net.Http;
using Seeing.Agent.MCP.Policy;
using Seeing.Agent.Middlewares;
using Seeing.Agent.Shell;
using Seeing.Agent.Skills;
using Seeing.Agent.Tools;
using Seeing.Agent.Tools.BuiltIn;
using Seeing.Agent.Tools.BuiltIn.FileSystem;
using Seeing.Agent.Tools.BuiltIn.Shell;
using Seeing.Agent.Tools.BuiltIn.SubTask;
using Seeing.Agent.Tools.BuiltIn.Time;
using Seeing.Agent.Tools.BuiltIn.Todo;
using Seeing.Agent.Tools.BuiltIn.Web;
using Seeing.Agent.Todo;
using Seeing.Session.Core;
using Seeing.Session.Management;
using Seeing.Session.Storage;

namespace Seeing.Agent.Extensions
{
    /// <summary>
    /// 服务集合扩展 - DI 注册入口
    /// </summary>
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// 注册 Seeing.Agent 核心服务
        /// </summary>
        public static IServiceCollection AddSeeingAgent(
            this IServiceCollection services,
            IConfiguration? configuration = null)
        {
            _ = configuration;

            // 注册 UnifiedConfigManager
            services.AddSingleton<UnifiedConfigManager>(sp =>
            {
                var workspace = sp.GetRequiredService<IWorkspaceProvider>();
                var logger = sp.GetRequiredService<ILogger<UnifiedConfigManager>>();
                var manager = new UnifiedConfigManager(workspace, logger);
                manager.LoadAsync().GetAwaiter().GetResult();
                return manager;
            });
            services.AddSingleton<IConfigSectionStore>(sp => sp.GetRequiredService<UnifiedConfigManager>());
            
            // IOptions 兼容 + IOptionsMonitor 支持热重载
            services.AddSingleton<SeeingAgentOptionsMonitor>();
            services.AddSingleton<IOptions<SeeingAgentOptions>>(sp => sp.GetRequiredService<SeeingAgentOptionsMonitor>());
            services.AddSingleton<IOptionsMonitor<SeeingAgentOptions>>(sp => sp.GetRequiredService<SeeingAgentOptionsMonitor>());
            services.AddSingleton<IOptions<GatewayOptions>, GatewayOptionsMonitor>();
            services.AddSingleton<IValidateOptions<GatewayOptions>, GatewayOptionsValidator>();
            services.AddSingleton<IValidateOptions<TokenBudgetOptions>, TokenBudgetOptionsValidator>();

            RegisterCoreServices(services);

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
        /// 注册 Seeing.Agent 核心服务（使用自定义配置）
        /// </summary>
        public static IServiceCollection AddSeeingAgent(
            this IServiceCollection services,
            Action<SeeingAgentOptions> configure)
        {
            // 注册 UnifiedConfigManager
            services.AddSingleton<UnifiedConfigManager>(sp =>
            {
                var workspace = sp.GetRequiredService<IWorkspaceProvider>();
                var logger = sp.GetRequiredService<ILogger<UnifiedConfigManager>>();
                var manager = new UnifiedConfigManager(workspace, logger);
                configure(manager.GetSeeingAgentOptions());
                return manager;
            });
            services.AddSingleton<IConfigSectionStore>(sp => sp.GetRequiredService<UnifiedConfigManager>());
            
            // IOptions 兼容 + IOptionsMonitor 支持热重载
            services.AddSingleton<SeeingAgentOptionsMonitor>();
            services.AddSingleton<IOptions<SeeingAgentOptions>>(sp => sp.GetRequiredService<SeeingAgentOptionsMonitor>());
            services.AddSingleton<IOptionsMonitor<SeeingAgentOptions>>(sp => sp.GetRequiredService<SeeingAgentOptionsMonitor>());
            services.AddSingleton<IOptions<GatewayOptions>, GatewayOptionsMonitor>();
            services.AddSingleton<IValidateOptions<GatewayOptions>, GatewayOptionsValidator>();
            services.AddSingleton<IValidateOptions<TokenBudgetOptions>, TokenBudgetOptionsValidator>();

            RegisterCoreServices(services);

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
            services.AddTransient<PermissionMiddleware>();
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

            // 注册命令发现器
            services.AddSingleton<CommandDiscovery>();

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
                services.AddSingleton<UnifiedConfigManager>(sp =>
                {
                    var workspace = sp.GetRequiredService<IWorkspaceProvider>();
                    var logger = sp.GetRequiredService<ILogger<UnifiedConfigManager>>();
                    var manager = new UnifiedConfigManager(workspace, logger);
                manager.LoadAsync().GetAwaiter().GetResult();
                return manager;
            });
                services.AddSingleton<IConfigSectionStore>(sp => sp.GetRequiredService<UnifiedConfigManager>());
                
            // IOptions 兼容 + IOptionsMonitor 支持热重载
            services.AddSingleton<SeeingAgentOptionsMonitor>();
            services.AddSingleton<IOptions<SeeingAgentOptions>, SeeingAgentOptionsMonitor>();
            services.AddSingleton<IOptionsMonitor<SeeingAgentOptions>, SeeingAgentOptionsMonitor>();
                services.AddSingleton<IOptions<GatewayOptions>, GatewayOptionsMonitor>();
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
            // 权限服务（新系统 - 统一权限检查入口）
            services.AddPermissionService();

            // Hook 管理器
            services.AddSingleton<HookManager>();
            services.AddSingleton<Seeing.Agent.Abstractions.Hooks.IHookManager>(sp => sp.GetRequiredService<HookManager>());
            // Session Hook 管理器适配器（让 SessionManager 使用）
            services.AddSingleton<Seeing.Session.Hooks.IHookManager>(sp =>
                new Seeing.Agent.Services.HookManagerAdapter(sp.GetRequiredService<HookManager>()));

            // 提示词构建服务
            services.AddPromptBuilder();

            // Agent 发现服务
            services.AddSingleton<AgentDiscovery>();

            // Agent 存储（纯存储操作）
            services.AddSingleton<AgentStore>();
            services.AddSingleton<IAgentStore>(sp => sp.GetRequiredService<AgentStore>());

            // Agent 运行时管理器（运行时设置）
            services.AddSingleton<AgentRuntimeManager>();
            services.AddSingleton<IAgentRuntimeManager>(sp => sp.GetRequiredService<AgentRuntimeManager>());

            // Agent 管理器（统一管理注册、发现、配置）
            services.AddSingleton<AgentManager>(sp =>
            {
                var logger = sp.GetRequiredService<ILogger<AgentManager>>();
                var agentStore = sp.GetRequiredService<IAgentStore>();
                var runtimeManager = sp.GetRequiredService<IAgentRuntimeManager>();
                var workspaceProvider = sp.GetRequiredService<IWorkspaceProvider>();
                var discovery = sp.GetRequiredService<AgentDiscovery>();
                var options = sp.GetService<IOptions<SeeingAgentOptions>>();

                // 获取内置代理
                var builtInAgents = BuiltInAgents.GetBuiltInAgents();

                // 从文件系统发现代理
                var discoveredAgents = discovery.DiscoverAgentsAsync().GetAwaiter().GetResult();
                var allAgents = builtInAgents.Concat(discoveredAgents);

                var manager = new AgentManager(
                    logger,
                    agentStore,
                    runtimeManager,
                    workspaceProvider,
                    allAgents,
                    defaultAgent: options?.Value?.DefaultAgent,
                    options: options);

                return manager;
            });
            // 兼容旧接口
            services.AddSingleton<IAgentRegistry>(sp => sp.GetRequiredService<AgentManager>());

            // AgentManager 自身实现 IHostedService，启动时自动加载 MD 配置
            services.AddHostedService<AgentManager>(sp => sp.GetRequiredService<AgentManager>());

            // Agent 运行时初始化服务（IHostedService）
            services.AddHostedService<AgentInitializationService>();

            // 会话管理器 - 必须注入 ISessionStore，否则 SaveAsync 只会警告「未配置 SessionStore」
            if (!services.Any(d => d.ServiceType == typeof(ISessionStore)))
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

            // 会话事件发布器（SessionManager / UI 必须共用同一实例）
            services.TryAddSingleton<ISessionEventPublisher, SessionEventPublisher>();

            // SessionManager 必须在 ISessionEventPublisher 注册之后，工厂解析时才能注入非 null publisher
            services.AddSingleton<SessionManager>(sp =>
                new SessionManager(
                    store: sp.GetRequiredService<ISessionStore>(),
                    hookManager: sp.GetService<Seeing.Session.Hooks.IHookManager>(),
                    eventPublisher: sp.GetRequiredService<ISessionEventPublisher>(),
                    logger: sp.GetService<ILogger<SessionManager>>(),
                    globalStore: sp.GetService<GlobalSessionStore>()));
            services.AddSingleton<ISessionManager>(sp =>
                sp.GetRequiredService<SessionManager>());

            // 压缩组件（主库唯一注册点，TokenBudget 扩展不重复注册）
            services.AddSingleton<ISummarizer, LlmSummarizer>();
            services.AddSingleton<CompressionService>();
            services.AddSingleton<CompressionOptions>();

            // 在线技能解析器
            services.AddSingleton<OnlineSkillParserAggregator>();

            // 技能管理器（集成配置的 Skills.Paths）
            services.AddSingleton<SkillManager>(sp =>
            {
                var logger = sp.GetRequiredService<ILogger<SkillManager>>();
                var workspace = sp.GetService<IWorkspaceProvider>();
                var options = sp.GetService<IOptions<SeeingAgentOptions>>();
                var manager = new SkillManager(logger, workspace: workspace);

                // 应用配置中的 Skills.Paths
                if (options?.Value?.Skills?.Paths != null)
                {
                    foreach (var path in options.Value.Skills.Paths)
                    {
                        manager.AddSearchDirectory(path);
                    }
                }

                // 远程技能 URL 暂不支持，记录警告
                if (options?.Value?.Skills?.Urls != null && options.Value.Skills.Urls.Count > 0)
                {
                    logger.LogWarning("远程技能 URL 暂不支持，已跳过 {Count} 个 URL", options.Value.Skills.Urls.Count);
                }

                return manager;
            });
            // 技能管理器接口映射（必须与具体类同处注册，避免 AddLlmProviders 分支跳过导致缺失）
            services.TryAddSingleton<ISkillManager>(sp => sp.GetRequiredService<SkillManager>());

            // 技能工具（让 LLM 加载技能内容）
            services.AddSingleton<ITool, SkillTool>();

            // 文件系统工具
            services.AddSingleton<ITool, ReadTool>();
            services.AddSingleton<ITool, WriteTool>();
            services.AddSingleton<ITool, EditTool>();
            services.AddSingleton<ITool, GlobTool>();
            services.AddSingleton<ITool, GrepTool>();
            services.AddSingleton<ITool, AddWorkspacePathTool>();
            services.AddSingleton<ITool, DeleteTool>();

            // Shell 工具
            services.AddSingleton<ITool, BashTool>();

            // 网络工具（需要 HttpClient）
            services.AddSingleton<ITool>(sp => new WebFetchTool(
                sp.GetRequiredService<ILogger<WebFetchTool>>(),
                sp.GetRequiredService<IHttpClientFactory>().CreateClient()));
            services.AddSingleton<ITool>(sp => new WebSearchTool(
                sp.GetRequiredService<ILogger<WebSearchTool>>(),
                sp.GetRequiredService<IHttpClientFactory>().CreateClient()));
            services.AddSingleton<ITool>(sp => new CodeSearchTool(
                sp.GetRequiredService<ILogger<CodeSearchTool>>(),
                sp.GetRequiredService<IHttpClientFactory>().CreateClient()));

            // 任务和 Todo 工具
            services.AddSingleton<ITool>(sp => new TaskTool(
                sp.GetRequiredService<ILogger<TaskTool>>(),
                sp.GetRequiredService<ISessionManager>(),
                sp.GetRequiredService<IAgentRegistry>(),
                sp.GetRequiredService<IAgentLoopScheduler>(),
                sp.GetRequiredService<ITaskEventProjector>(),
                sp.GetService<ISessionEventBus>()));
            services.AddSingleton<ITool>(sp => new TaskStatusTool(
                sp.GetRequiredService<ILogger<TaskStatusTool>>(),
                sp.GetRequiredService<ISessionManager>()));
            services.AddSingleton<ITool, TodoWriteTool>();

            // 时间工具
            services.AddSingleton<ITool, CurrentTimeTool>();

            // ========== 注册装饰器链（超时→重试→缓存）==========
            services.AddSingleton<IToolDecoratorRegistry>(sp =>
            {
                var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
                var cache = sp.GetService<IMemoryCache>();

                var registry = new ToolDecoratorRegistry(sp);

                // 最外层：超时装饰器（30 秒默认）
                registry.Register(tool => new TimeoutToolDecorator(
                    tool,
                    TimeSpan.FromSeconds(30),
                    loggerFactory.CreateLogger<TimeoutToolDecorator>()));

                // 中间层：重试装饰器（3 次，1 秒间隔，指数退避）
                registry.Register(tool => new RetryToolDecorator(
                    tool,
                    maxRetries: 3,
                    delay: TimeSpan.FromSeconds(1),
                    logger: loggerFactory.CreateLogger<RetryToolDecorator>()));

                // 最内层：缓存装饰器（5 分钟过期）— 仅在 IMemoryCache 可用时
                if (cache != null)
                {
                    registry.Register(tool => new CachedToolDecorator(
                        tool,
                        cache,
                        TimeSpan.FromMinutes(5),
                        loggerFactory.CreateLogger<CachedToolDecorator>()));
                }

                return registry;
            });

            // 工具权限策略
            services.TryAddSingleton<IToolPermissionPolicy, DefaultToolPermissionPolicy>();

            // 工具调用器（自动注册所有 ITool）
            services.AddSingleton<ToolManager>(sp =>
            {
                var logger = sp.GetRequiredService<ILogger<ToolManager>>();
                var hookManager = sp.GetRequiredService<Seeing.Agent.Abstractions.Hooks.IHookManager>();
                var tools = sp.GetServices<ITool>();
                var decoratorRegistry = sp.GetService<IToolDecoratorRegistry>();
                var workspace = sp.GetService<IWorkspaceProvider>();
                var permissionPolicy = sp.GetService<IToolPermissionPolicy>();

                var invoker = new ToolManager(logger, hookManager, sp, decoratorRegistry,
                    workspace: workspace, permissionPolicy: permissionPolicy);

                // 自动注册所有 ITool
                foreach (var tool in tools)
                {
                    invoker.RegisterTool(tool);
                }

                return invoker;
            });

            // === MCP 服务注册 ===

            // 1. 全局策略配置（从 IConfiguration 加载）
            services.AddSingleton<McpGlobalPolicy>(sp =>
            {
                var config = sp.GetService<IConfiguration>()?.GetSection("SeeingAgent:Mcp");
                return new McpGlobalPolicy
                {
                    ConnectionTimeout = TimeSpan.FromSeconds(config?.GetValue("ConnectionTimeoutSeconds", 30) ?? 30),
                    OperationTimeout = TimeSpan.FromSeconds(config?.GetValue("OperationTimeoutSeconds", 60) ?? 60),
                    BackgroundCheckInterval = TimeSpan.FromSeconds(config?.GetValue("BackgroundCheckIntervalSeconds", 10) ?? 10),
                    MaxConcurrentConnections = config?.GetValue("MaxConcurrentConnections", 3) ?? 3,
                    AutoStartOnAdd = config?.GetValue("AutoStartOnAdd", true) ?? true
                };
            });

            // 2. 工厂注册表
            services.AddSingleton<McpWrapperFactoryRegistry>(sp =>
            {
                var registry = new McpWrapperFactoryRegistry();
                registry.Register(new Seeing.Agent.MCP.Factory.StdioWrapperFactory());
                registry.Register(new Seeing.Agent.MCP.Factory.HttpWrapperFactory(HttpTransportMode.StreamableHttp));
                registry.Register(new Seeing.Agent.MCP.Factory.HttpWrapperFactory(HttpTransportMode.Sse));
                return registry;
            });

            // 3. 工具注册管理（内部服务）
            services.AddSingleton<McpToolRegistry>(sp =>
            {
                var toolInvoker = sp.GetRequiredService<ToolManager>();
                var hookManager = sp.GetRequiredService<Seeing.Agent.Abstractions.Hooks.IHookManager>();
                var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<McpToolRegistry>();
                return new McpToolRegistry(toolInvoker, hookManager, logger);
            });

            // 4. 进程监控（内部服务）
            services.AddSingleton<McpProcessMonitor>(sp =>
            {
                var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<McpProcessMonitor>();
                return new McpProcessMonitor(logger);
            });

            // 5. 工作区路径提供者（统一管理配置目录）
            // 初始化时根据配置自动解析工作区
            services.AddSingleton<WorkspaceProvider>();
            services.AddSingleton<IWorkspaceProvider>(sp => sp.GetRequiredService<WorkspaceProvider>());

            // 5.1 统一重载编排器（订阅配置/工作区变更，调度所有 IReloadHandler）
            services.AddSingleton<ReloadOrchestrator>();
            // 插件推送与动态注册入口：均指向编排器，扩展包只引用 Abstractions 接口
            services.AddSingleton<IReloadSignalBus>(sp => sp.GetRequiredService<ReloadOrchestrator>());
            services.AddSingleton<IReloadHandlerRegistry>(sp => sp.GetRequiredService<ReloadOrchestrator>());

            // 5.2 Todo 存储（端口-适配器：基于 Session Context 的默认实现）
            services.AddSingleton<ITodoStore, SessionContextTodoStore>();

            // 6. Agent / Model 默认解析
            services.AddSingleton<AgentSelectionResolver>();

            // 6. MCP 配置持久化服务
            services.AddSingleton<IMcpConfigPersistence, McpConfigPersistence>();

            // 7. MCP 客户端管理器（实现 IMcpManager 接口）
            services.AddSingleton<IMcpManager, McpClientManager>();
            services.AddSingleton<McpClientManager>(sp => (McpClientManager)sp.GetRequiredService<IMcpManager>());

            // 扩展系统
            services.AddSingleton<ExtensionLoader>();
            services.AddSingleton<ExtensionManager>();
            services.AddSingleton<IExtensionManager>(sp => sp.GetRequiredService<ExtensionManager>());

            // 执行上下文相关
            services.AddSingleton<IMetadataStore, ConcurrentMetadataStore>();

            // 权限记忆（会话级，纯内存）
            services.AddSingleton<IPermissionMemory, SessionPermissionMemory>();

            // 会话级工作区白名单（AddWorkspacePathTool 写入，权限通道读取）
            services.AddSingleton<IWorkspaceWhitelist, SessionWorkspaceWhitelist>();

            // 权限通道 — 默认使用 DynamicPermissionChannel + SerializingPermissionChannel（带记忆）
            // 使用 IOptionsMonitor 实现运行时配置变更无需重启
            // 注意：如果用户在其他地方注册了 IPermissionChannel（如 BlazorPermissionChannel），
            // 那个注册会覆盖这里的默认注册
            services.AddSingleton<IPermissionChannel>(sp =>
            {
                var memory = sp.GetRequiredService<IPermissionMemory>();
                var workspace = sp.GetService<IWorkspaceProvider>();
                var optionsMonitor = sp.GetRequiredService<IOptionsMonitor<SeeingAgentOptions>>();
                var logger = sp.GetService<ILogger<Core.Permission.DynamicPermissionChannel>>();

                // 动态通道：每次请求时从 IOptionsMonitor 读取最新配置
                var inner = new Core.Permission.DynamicPermissionChannel(optionsMonitor, logger);

                // 进程级 Ask 串行 + 会话级记忆 + 工作区边界检查（宿主可再包一层，如 Blazor）
                return new Core.Permission.SerializingPermissionChannel(inner, memory, workspace,
                    sp.GetRequiredService<Core.Permission.IWorkspaceWhitelist>());
            });

            // Agent 执行器（统一执行引擎）
            services.AddSingleton<AgentExecutor>();

            // Agent 执行器契约（Native 默认实现，ACP 包可替换）
            services.AddSingleton<IAgentExecutor, NativeAgentExecutor>();

            services.AddSingleton<
                Seeing.Agent.Services.ISessionTitleService,
                Seeing.Agent.Services.SessionTitleService>();

            // Shell 环境服务（触发 shell.env Hook）
            services.AddSingleton<IShellEnvironmentService, ShellEnvironmentService>();

            // Shell 服务（跨平台 Shell 选择和进程管理）
            services.AddSingleton<IShellService, ShellService>();

            // 命令执行服务（触发 command.execute.before Hook）
            services.AddSingleton<ICommandService, CommandService>();

            // 后台任务管理器（HostedService：进程优雅停止时 CancelAll）
            services.AddSingleton<BackgroundTaskManager>();
            services.AddSingleton<IBackgroundTaskManager>(sp => sp.GetRequiredService<BackgroundTaskManager>());
            services.AddHostedService(sp => sp.GetRequiredService<BackgroundTaskManager>());
            services.AddSingleton<IAgentLoopScheduler, AgentLoopScheduler>();
            services.AddSingleton<ITaskEventProjector, TaskEventProjector>();

            // 命令注册表（插件加载与扩展命令注册需要）
            services.AddSingleton<ICommandRegistry, CommandRegistry>();

            // 组件管理器（统一管理 Skills/MCP/Plugins/Rules）
            services.AddSingleton<IComponentManager, ComponentManager>();
        }

        /// <summary>
        /// 注册 LLM 服务
        /// </summary>
        private static void RegisterLlmServices(IServiceCollection services)
        {
            // LLM 客户端工厂
            services.AddSingleton<ILlmClientFactory, DefaultLlmClientFactory>();

            // ModelManager 依赖 IAgentStore（非 IAgentRegistry），避免环：
            // ModelManager → AgentManager → AgentRuntimeManager → IModelManager
            services.AddSingleton<ModelConfigManager>();
            services.AddSingleton<ModelManager>(sp => new ModelManager(
                sp.GetRequiredService<ModelConfigManager>(),
                sp.GetRequiredService<IAgentStore>()));
            services.AddSingleton<IModelManager>(sp => sp.GetRequiredService<ModelManager>());
            services.AddSingleton<IModelConfigManager>(sp => sp.GetRequiredService<ModelManager>());

            // Provider 管理器
            services.AddSingleton<IProviderRegistry, ProviderRegistry>();
            services.AddSingleton<IProviderManager, ProviderManager>();

            // LLM 服务（调用层）
            services.AddSingleton<ILlmService, LlmService>();
            services.AddSingleton<ITextCompletion, TextCompletionService>();
            services.AddSingleton<IProviderEndpointLookup, OptionsProviderEndpointLookup>();
        }

        /// <summary>
        /// 添加提示词构建服务
        /// </summary>
        public static IServiceCollection AddPromptBuilder(this IServiceCollection services)
        {
            services.AddSingleton<IInstructionManager, InstructionManager>();
            services.AddSingleton<PromptBuilder>();

            return services;
        }
    }

        /// <summary>
        /// 默认 LLM 客户端工厂
        /// </summary>
        internal class DefaultLlmClientFactory : ILlmClientFactory
        {
            private readonly ILoggerFactory _loggerFactory;

            public DefaultLlmClientFactory(ILoggerFactory loggerFactory)
            {
                _loggerFactory = loggerFactory;
            }

            public IReadOnlyList<ProviderType> SupportedTypes { get; } = new[]
            {
                ProviderType.OpenAI,
                ProviderType.Anthropic
            };

            public bool SupportsType(ProviderType type) => SupportedTypes.Contains(type);

            public ILlmClient Create(ProviderConfig config)
            {
                ArgumentNullException.ThrowIfNull(config);
                // 每个 Provider 使用自己的 handler，确保 Proxy/UseProxy 在创建时生效。
                var httpClient = LlmHttpClientFactory.Create(config);

                return config.Type switch
                {
                    ProviderType.OpenAI => new OpenAiChatClient(
                        config,
                        httpClient,
                        _loggerFactory.CreateLogger<OpenAiChatClient>()),
                    ProviderType.Anthropic => new AnthropicClient(
                        config,
                        httpClient,
                        _loggerFactory.CreateLogger<AnthropicClient>()),
                    _ => throw new NotSupportedException($"不支持的 Provider 类型: {config.Type}")
                };
            }
        }
}

namespace Seeing.Agent.Extensions
{
    /// <summary>
    /// 初始化扩展 - 使用统一组件管理器加载所有组件
    /// </summary>
    public static class SeeingAgentInitializationExtensions
    {
        /// <summary>
        /// 初始化 Seeing.Agent - 通过 ComponentManager 加载 Skills/MCP/Plugins/Rules
        /// </summary>
        /// <param name="services">服务提供者</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>各组件加载结果</returns>
        public static async Task<IReadOnlyList<ComponentLoadResult>> InitializeSeeingAgentAsync(
            this IServiceProvider services,
            CancellationToken cancellationToken = default)
        {
            var loggerFactory = services.GetService<ILoggerFactory>();
            var logger = loggerFactory?.CreateLogger(typeof(SeeingAgentInitializationExtensions));

            // 初始化工作区（自动根据配置解析）
            if (services.GetService<WorkspaceProvider>() is { } workspaceProvider)
            {
                var configManager = services.GetService<UnifiedConfigManager>();
                var workspaceLogger = services.GetService<ILogger<WorkspaceProvider>>();

                if (configManager != null)
                {
                    workspaceProvider.SetDependencies(configManager, workspaceLogger);
                    await workspaceProvider.InitializeAsync(cancellationToken);
                }
            }

            if (services.GetService<UnifiedConfigManager>() is { } configManager2)
            {
                await configManager2.ReloadAsync(cancellationToken);
            }

            var componentManager = services.GetRequiredService<IComponentManager>();
            var workspaceRoot = services.GetRequiredService<IWorkspaceProvider>().WorkspaceRoot;
            var results = await componentManager.LoadAllAsync(workspaceRoot, cancellationToken);

            // 加载工具/技能禁用状态
            var toolInvoker = services.GetService<ToolManager>();
            if (toolInvoker != null)
            {
                await toolInvoker.LoadToolStateAsync(cancellationToken);
            }

            var skillManager = services.GetService<SkillManager>();
            if (skillManager != null)
            {
                await skillManager.LoadSkillStateAsync(cancellationToken);
            }

            return results;
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
