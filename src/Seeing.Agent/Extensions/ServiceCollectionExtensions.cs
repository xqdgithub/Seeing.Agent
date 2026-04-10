using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Commands;
using Seeing.Agent.Commands.Discovery;
using Seeing.Agent.Core;
using Seeing.Agent.Core.Background;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.BuiltInAgents;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Decorators;
using Seeing.Agent.Hooks;
using Seeing.Agent.Llm;
using Seeing.Agent.Llm.Clients;
using Seeing.Agent.Middlewares;
using Seeing.Agent.MCP;
using Seeing.Agent.Rules;
using Seeing.Agent.Sessions;
using Seeing.Agent.Shell;
using Seeing.Agent.Skills;
using Seeing.Agent.Tools;
using Seeing.Agent.Tools.BuiltIn.FileSystem;
using Seeing.Agent.Tools.BuiltIn.Shell;
using Seeing.Agent.Tools.BuiltIn.SubTask;
using Seeing.Agent.Tools.BuiltIn.Todo;
using Seeing.Agent.Tools.BuiltIn.Web;

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
            IConfiguration configuration)
        {
            // 注册配置选项
            services.Configure<SeeingAgentOptions>(
                configuration.GetSection("SeeingAgent"));

            // 注册核心接口和实现
            RegisterCoreServices(services);

            // 注册 LLM 服务
            RegisterLlmServices(services);

            // 注册 HttpClient 工厂
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
            services.Configure(configure);

            // 注册核心接口和实现
            RegisterCoreServices(services);

            // 注册 LLM 服务
            RegisterLlmServices(services);

            // 注册 HttpClient 工厂
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
            
            // 工具发现和注册在 ToolInvoker 中完成
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

            // 注册命令分发器（需要 CommandService 用于 Hook）
            services.AddSingleton<CommandDispatcher>(sp =>
            {
                var registry = sp.GetRequiredService<ICommandRegistry>();
                var commandService = sp.GetService<ICommandService>();
                var logger = sp.GetService<ILogger<CommandDispatcher>>();
                return new CommandDispatcher(registry, commandService, logger);
            });

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
            IConfiguration configuration)
        {
            // 注册配置选项
            services.Configure<SeeingAgentOptions>(
                configuration.GetSection("SeeingAgent"));

            // 注册 LLM 服务
            RegisterLlmServices(services);

            // 注册 HttpClient
            services.AddHttpClient();

            return services;
        }

        /// <summary>
        /// 注册核心服务
        /// </summary>
        private static void RegisterCoreServices(IServiceCollection services)
        {
            // 配置持久化服务
            services.AddSingleton<IConfigurationPersistence, ConfigurationPersistence>();

            // 权限评估器（具体类型与接口共享同一单例，避免双实例）
            services.AddSingleton<RuleEngine>();
            services.AddSingleton<IRuleEvaluator>(sp => sp.GetRequiredService<RuleEngine>());
            services.AddSingleton<IRuleEngine>(sp => sp.GetRequiredService<RuleEngine>());

            // Hook 管理器
            services.AddSingleton<HookManager>();
            services.AddSingleton<IHookManager>(sp => sp.GetRequiredService<HookManager>());

            // Agent 发现服务
            services.AddSingleton<AgentDiscovery>();

            // Agent 存储（纯存储操作）
            services.AddSingleton<AgentStore>();
            services.AddSingleton<IAgentStore>(sp => sp.GetRequiredService<AgentStore>());

            // Agent 运行时管理器（运行时设置）
            services.AddSingleton<AgentRuntimeManager>();
            services.AddSingleton<IAgentRuntimeManager>(sp => sp.GetRequiredService<AgentRuntimeManager>());

            // Agent 注册表（协调者）
            services.AddSingleton<AgentRegistry>(sp =>
            {
                var logger = sp.GetRequiredService<ILogger<AgentRegistry>>();
                var ruleEngine = sp.GetRequiredService<IRuleEngine>();
                var agentStore = sp.GetRequiredService<IAgentStore>();
                var runtimeManager = sp.GetRequiredService<IAgentRuntimeManager>();
                var discovery = sp.GetRequiredService<AgentDiscovery>();
                var options = sp.GetService<IOptions<SeeingAgentOptions>>();

                // 获取内置代理
                var builtInAgents = BuiltInAgents.GetBuiltInAgents();

                // 从文件系统发现代理
                var discoveredAgents = discovery.DiscoverAgentsAsync().GetAwaiter().GetResult();
                var allAgents = builtInAgents.Concat(discoveredAgents);

                var registry = new AgentRegistry(
                    logger,
                    ruleEngine,
                    agentStore,
                    runtimeManager,
                    allAgents,
                    options?.Value?.DefaultAgent);

                // 从配置扩展代理
                if (options?.Value?.Agents != null)
                {
                    registry.ExtendFromConfig(options.Value.Agents);
                }

                return registry;
            });
            services.AddSingleton<IAgentRegistry>(sp => sp.GetRequiredService<AgentRegistry>());

            // Agent 初始化服务（IHostedService）- 异步初始化运行时设置
            services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService, AgentInitializationService>();

            // 会话管理器（同一 Scoped 实例）
            services.AddScoped<SessionManager>();
            services.AddScoped<ISessionManager>(sp => sp.GetRequiredService<SessionManager>());

            // 技能管理器（集成配置的 Skills.Paths）
            services.AddSingleton<SkillManager>(sp =>
            {
                var logger = sp.GetRequiredService<ILogger<SkillManager>>();
                var options = sp.GetService<IOptions<SeeingAgentOptions>>();
                var manager = new SkillManager(logger);

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

            // 技能工具（让 LLM 加载技能内容）
            services.AddSingleton<ITool, SkillTool>();

            // 文件系统工具
            services.AddSingleton<ITool, ReadTool>();
            services.AddSingleton<ITool, WriteTool>();
            services.AddSingleton<ITool, EditTool>();
            services.AddSingleton<ITool, GlobTool>();
            services.AddSingleton<ITool, GrepTool>();

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
                sp.GetRequiredService<IAgentRegistry>()));
            services.AddSingleton<ITool, TodoWriteTool>();

            // 工具调用器（自动注册所有 ITool）
            services.AddSingleton<ToolInvoker>(sp =>
            {
                var logger = sp.GetRequiredService<ILogger<ToolInvoker>>();
                var hookManager = sp.GetRequiredService<IHookManager>();
                var tools = sp.GetServices<ITool>();
                var decoratorRegistry = sp.GetService<IToolDecoratorRegistry>();

                var invoker = new ToolInvoker(logger, hookManager, sp, decoratorRegistry);

                // 自动注册所有 ITool
                foreach (var tool in tools)
                {
                    invoker.RegisterTool(tool);
                }

                return invoker;
            });

            // MCP 客户端管理器（支持 stdio 和 HTTP 传输）
            services.AddSingleton<McpClientManager>(sp =>
            {
                var logger = sp.GetRequiredService<ILogger<McpClientManager>>();
                var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
                var httpClientFactory = sp.GetService<IHttpClientFactory>();
                return new McpClientManager(logger, loggerFactory, httpClientFactory);
            });

            // 扩展系统
            services.AddSingleton<ExtensionLoader>();
            services.AddSingleton<ExtensionManager>();

            // 执行上下文相关
            services.AddSingleton<IMetadataStore, ConcurrentMetadataStore>();

            // 权限通道 - 根据配置选择安全模式
            // 注意：如果用户在其他地方注册了 IPermissionChannel（如 ConsolePermissionChannel），
            // 那个注册会覆盖这里的默认注册
            services.AddSingleton<IPermissionChannel>(sp =>
            {
                var options = sp.GetService<IOptions<SeeingAgentOptions>>();
                var autoApprove = options?.Value?.Permission?.AutoApproveAll ?? false;

                if (autoApprove)
                {
                    // 用户明确选择自动批准所有（危险模式）
                    return Core.Interfaces.DefaultPermissionChannel.AutoApproveInstance;
                }

                // 安全默认：拒绝所有，提示用户配置权限通道
                return Core.Interfaces.DefaultPermissionChannel.Instance;
            });

            // Agent 执行器（统一执行引擎）
            services.AddSingleton<AgentExecutor>();

            // Shell 环境服务（触发 shell.env Hook）
            services.AddSingleton<IShellEnvironmentService, ShellEnvironmentService>();

            // Shell 服务（跨平台 Shell 选择和进程管理）
            services.AddSingleton<IShellService, ShellService>();

            // 命令执行服务（触发 command.execute.before Hook）
            services.AddSingleton<ICommandService, CommandService>();

            // 后台任务管理器
            services.AddSingleton<BackgroundTaskManager>();
            services.AddSingleton<IBackgroundTaskManager>(sp => sp.GetRequiredService<BackgroundTaskManager>());
        }

        /// <summary>
        /// 注册 LLM 服务
        /// </summary>
        private static void RegisterLlmServices(IServiceCollection services)
        {
            // LLM 客户端工厂
            services.AddSingleton<ILlmClientFactory, DefaultLlmClientFactory>();
            
            // LLM 服务
            services.AddSingleton<ILlmService, LlmService>();
        }
    }

    /// <summary>
    /// 默认 LLM 客户端工厂
    /// </summary>
    internal class DefaultLlmClientFactory : ILlmClientFactory
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILoggerFactory _loggerFactory;

        public DefaultLlmClientFactory(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
        {
            _httpClientFactory = httpClientFactory;
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
            return config.Type switch
            {
                ProviderType.OpenAI => new OpenAiClient(config, _loggerFactory.CreateLogger<OpenAiClient>()),
                ProviderType.Anthropic => new AnthropicClient(
                    config,
                    _httpClientFactory.CreateClient(),
                    _loggerFactory.CreateLogger<AnthropicClient>()),
                _ => throw new NotSupportedException($"不支持的 Provider 类型: {config.Type}")
            };
        }
    }
}
