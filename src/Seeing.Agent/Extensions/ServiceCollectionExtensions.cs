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
using Seeing.Agent.Commands;
using Seeing.Agent.Commands.Discovery;
using Seeing.Agent.Compression;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Agents.BuiltIn;
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
using Seeing.Agent.Core.Todo;
using Seeing.Agent.Decorators;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Execution;
using Seeing.IO.Local;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Tools.Basic;
using Seeing.Agent.Tools.FileSystem;
using Seeing.Agent.Tools.Shell;
using Seeing.Agent.Tools.Web;
using Seeing.Agent.Tools.Git;
using Seeing.Agent.Skills;
using Seeing.Agent.Mcp;
using Seeing.Agent.Llm;
using Seeing.Agent.Llm.OpenAI;
using Seeing.Agent.Llm.Anthropic;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Mcp;
using Seeing.Agent.Abstractions.Summarization;
using System.Net.Http;
using Seeing.Agent.Middlewares;
using Seeing.Agent.Shell;
using Seeing.Agent.Tools;
using Seeing.Agent.Tools.BuiltIn;
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
            // TEMP: Phase 2 — 本地执行世界，待模块结算落地后改由 LocalExecutionWorldModule Activate
            services.TryAddSingleton<IExecutionWorld, LocalExecutionWorld>();

            // TEMP: Phase 3 — FileSystem 工具模块（ConfigureServices 注册 ITool；Activate 待 Host Shape）
            var fileSystemModule = new FileSystemModule();
            fileSystemModule.ConfigureServices(services);
            services.AddSingleton<ISeeingModule>(fileSystemModule);

            // TEMP: Phase 3 — Web 工具模块（ConfigureServices 注册 ITool；Activate 待 Host Shape）
            var webModule = new WebModule();
            webModule.ConfigureServices(services);
            services.AddSingleton<ISeeingModule>(webModule);

            // TEMP: Phase 3 — Shell 工具模块（ConfigureServices 注册 ITool + IShellService；Activate 待 Host Shape）
            services.TryAddSingleton<IOptionsMonitor<ShellOptions>>(sp =>
                new ShellOptionsMonitor(sp.GetRequiredService<IOptionsMonitor<SeeingAgentOptions>>()));
            var shellModule = new ShellModule();
            shellModule.ConfigureServices(services);
            services.AddSingleton<ISeeingModule>(shellModule);

            // TEMP: Phase 3 — Basic 工具模块（ConfigureServices 注册 ITool；Activate 待 Host Shape）
            var basicModule = new BasicModule();
            basicModule.ConfigureServices(services);
            services.AddSingleton<ISeeingModule>(basicModule);

            // TEMP: Phase 3 — Git 工具模块（ConfigureServices 注册 IGitService + ITool；Activate 待 Host Shape）
            var gitModule = new GitModule();
            gitModule.ConfigureServices(services);
            services.AddSingleton<ISeeingModule>(gitModule);

            // TEMP: Phase 3 — Skills 模块（ConfigureServices 注册 SkillManager + parsers + skill；Activate 待 Host Shape）
            var skillsModule = new SkillsModule();
            skillsModule.ConfigureServices(services);
            services.AddSingleton<ISeeingModule>(skillsModule);

            // TEMP: Phase 3 — MCP 模块登记（ConfigureServices 在 ToolManager 注册后调用；见下方）
            var mcpModule = new McpModule();
            services.AddSingleton<ISeeingModule>(mcpModule);

            // TEMP: Phase 3 — LLM 工厂模块（ConfigureServices 注册 ILlmClientFactory；Activate 待 Host Shape）
            var openAiLlmModule = new OpenAiLlmModule();
            openAiLlmModule.ConfigureServices(services);
            services.AddSingleton<ISeeingModule>(openAiLlmModule);

            var anthropicLlmModule = new AnthropicLlmModule();
            anthropicLlmModule.ConfigureServices(services);
            services.AddSingleton<ISeeingModule>(anthropicLlmModule);

            // TEMP: Phase 3 — 内置 Agent 模块（ConfigureServices 注册 AgentDefinition；Activate 待 Host Shape）
            var agentsBuiltInModule = new AgentsBuiltInModule();
            agentsBuiltInModule.ConfigureServices(services);
            services.AddSingleton<ISeeingModule>(agentsBuiltInModule);

            services.TryAddSingleton<IFileSystem>(sp => sp.GetRequiredService<IExecutionWorld>().FileSystem);
            services.TryAddSingleton<ISubprocessFactory>(sp => sp.GetRequiredService<IExecutionWorld>().Subprocess);

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

                // 内置代理由 AgentsBuiltInModule.ConfigureServices 登记为 AgentDefinition
                var builtInAgents = sp.GetServices<AgentDefinition>();

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

            // 工具输出落盘服务（超限工具输出写会话 ref 目录）
            services.AddSingleton<Seeing.Agent.Output.IToolOutputStore>(sp =>
                new Seeing.Agent.Output.SessionToolOutputStore(
                    sp.GetRequiredService<ISessionStore>(),
                    sp.GetService<ILogger<Seeing.Agent.Output.SessionToolOutputStore>>()));

            // 压缩组件（主库唯一注册点，TokenBudget 扩展不重复注册）
            services.AddSingleton<ISummarizer, LlmSummarizer>();
            services.AddSingleton<CompressionService>();
            services.AddSingleton<CompressionOptions>();

            // 技能 / 在线解析器 / skill 工具 — 由 SkillsModule.ConfigureServices 注册（见上方模块登记）

            // 文件系统工具 — 由 FileSystemModule.ConfigureServices 注册（见上方模块登记）
            // Shell 工具 — 由 ShellModule.ConfigureServices 注册（见上方模块登记）

            // 网络工具 — 由 WebModule.ConfigureServices 注册（见上方模块登记）

            // Git 工具 — 由 GitModule.ConfigureServices 注册（见上方模块登记）

            // 任务和 Todo 工具 — 由 Seeing.Agent.Hosting.AddChatOrchestrator 注册

            // 基础工具 — 由 BasicModule.ConfigureServices 注册（见上方模块登记）

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
                    sp.GetRequiredService<Seeing.Agent.Output.IToolOutputStore>(),
                    sp.GetRequiredService<IWorkspaceWhitelist>(),
                    loggerFactory.CreateLogger<ToolOutputLimiterDecorator>()));

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
            services.AddSingleton<IToolManager>(sp => sp.GetRequiredService<ToolManager>());

            // TEMP: Phase 3 — MCP 模块（ConfigureServices 注册 IMcpManager 等；Activate 待 Host Shape）
            // 在 IToolManager 注册之后调用，以便工厂可解析工具管理器。
            mcpModule.ConfigureServices(services);

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

            // 6. Agent / Model 默认解析
            services.AddSingleton<AgentSelectionResolver>();

            // MCP — 由 McpModule.ConfigureServices 注册（见上方模块登记）

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
                    sp.GetRequiredService<IWorkspaceWhitelist>());
            });

            // Agent 执行器（统一执行引擎）
            services.AddSingleton<AgentExecutor>();

            // Native 实现 + Router 门面（ACP 等包追加 IAgentExecutorImplementation，不替换 IAgentExecutor）
            services.AddSingleton<IAgentExecutorImplementation, NativeAgentExecutor>();
            services.AddSingleton<IAgentExecutor, AgentExecutorRouter>();

            services.AddSingleton<
                Seeing.Agent.Services.ISessionTitleService,
                Seeing.Agent.Services.SessionTitleService>();

            // Shell 环境服务（触发 shell.env Hook）
            services.AddSingleton<IShellEnvironmentService, ShellEnvironmentService>();

            // 命令执行服务（触发 command.execute.before Hook）
            services.AddSingleton<ICommandService, CommandService>();

            services.AddSingleton<IAgentLoopScheduler, AgentLoopScheduler>();

            // 命令注册表（插件加载与扩展命令注册需要）
            services.AddSingleton<ICommandRegistry, CommandRegistry>();

            // 组件管理器（统一管理 Skills/MCP/Plugins/Rules）
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

            // Provider 管理器（同时注册具体类，供 ProviderReloadHandler 等注入）
            services.AddSingleton<IProviderRegistry, ProviderRegistry>();
            services.AddSingleton<ProviderManager>();
            services.AddSingleton<IProviderManager>(sp => sp.GetRequiredService<ProviderManager>());

            // LLM 服务（调用层）
            services.AddSingleton<ILlmService, LlmService>();
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
            services.AddSingleton<IReloadHandler, AgentRuntimeReloadHandler>();
            services.AddSingleton<IReloadHandler, AgentManagerReloadHandler>();
            services.AddSingleton<IReloadHandler, SessionReloadHandler>();
            services.AddSingleton<IReloadHandler>(sp => sp.GetRequiredService<ComponentManager>());
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
            var workspaceRoot = services.GetRequiredService<IWorkspaceProvider>().GetProjectRoot();

            // Interim: apply Skills.Paths from SeeingAgentOptions before discovery (SkillsOptions Phase 4)
            var skillManager = services.GetService<SkillManager>();
            if (skillManager != null)
            {
                var options = services.GetService<IOptions<SeeingAgentOptions>>();
                if (options?.Value?.Skills?.Paths != null)
                {
                    foreach (var path in options.Value.Skills.Paths)
                    {
                        skillManager.AddSearchDirectory(path);
                    }
                }

                if (options?.Value?.Skills?.Urls != null && options.Value.Skills.Urls.Count > 0)
                {
                    logger?.LogWarning("远程技能 URL 暂不支持，已跳过 {Count} 个 URL", options.Value.Skills.Urls.Count);
                }
            }

            var results = await componentManager.LoadAllAsync(workspaceRoot, cancellationToken);

            // 加载工具/技能禁用状态
            var toolInvoker = services.GetService<ToolManager>();
            if (toolInvoker != null)
            {
                await toolInvoker.LoadToolStateAsync(cancellationToken);
            }

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
