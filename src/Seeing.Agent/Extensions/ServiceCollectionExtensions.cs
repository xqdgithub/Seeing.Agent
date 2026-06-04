using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using Seeing.Agent.Commands;
using Seeing.Agent.Commands.Discovery;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core;
using Seeing.Agent.Core.Background;
using Seeing.Agent.Core.BuiltInAgents;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Core.Instructions;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Core.Prompts;
using Seeing.Agent.Decorators;
using Seeing.Agent.Llm;
using Seeing.Agent.Llm.Clients;
using Seeing.Agent.MCP;
using Seeing.Agent.MCP.Configuration;
using Seeing.Agent.MCP.Core;
using Seeing.Agent.MCP.Factory;
using Seeing.Agent.MCP.Management;
using Seeing.Agent.MCP.Policy;
using Seeing.Agent.Middlewares;
using Seeing.Agent.Shell;
using Seeing.Agent.Skills;
using Seeing.Agent.Tools;
using Seeing.Agent.Tools.BuiltIn.FileSystem;
using Seeing.Agent.Tools.BuiltIn.Shell;
using Seeing.Agent.Tools.BuiltIn.SubTask;
using Seeing.Agent.Tools.BuiltIn.Todo;
using Seeing.Agent.Tools.BuiltIn.Web;
using Seeing.Session.Core;

namespace Seeing.Agent.Extensions
{
    /// <summary>
    /// 服务集合扩展 - DI 注册入口
    /// </summary>
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// 配置反序列化选项（支持字符串到枚举的转换）
        /// </summary>
        private static readonly System.Text.Json.JsonSerializerOptions ConfigJsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        /// <summary>
        /// 注册 Seeing.Agent 核心服务
        /// </summary>
        public static IServiceCollection AddSeeingAgent(
            this IServiceCollection services,
            IConfiguration configuration)
        {
            // 获取日志记录器（用于配置加载过程）
            var loggerFactory = services.BuildServiceProvider().GetService<ILoggerFactory>();
            var logger = loggerFactory?.CreateLogger(typeof(ServiceCollectionExtensions));

            // 从用户配置文件加载配置
            var options = LoadUserConfiguration(logger);

            // 合并 IConfiguration 中的配置（覆盖用户配置）
            var configSection = configuration.GetSection("SeeingAgent");
            if (configSection.Exists())
            {
                var configOptions = configSection.Get<SeeingAgentOptions>();
                if (configOptions != null)
                {
                    // 合并 Providers
                    foreach (var (key, value) in configOptions.Providers)
                        options.Providers[key] = value;
                    // 合并 Models
                    if (configOptions.Models != null)
                    {
                        foreach (var (key, value) in configOptions.Models)
                            options.Models![key] = value;
                    }
                    // 合并其他配置
                    if (!string.IsNullOrEmpty(configOptions.DefaultProvider))
                        options.DefaultProvider = configOptions.DefaultProvider;
                    if (!string.IsNullOrEmpty(configOptions.DefaultModel))
                        options.DefaultModel = configOptions.DefaultModel;
                    if (!string.IsNullOrEmpty(configOptions.DefaultAgent))
                        options.DefaultAgent = configOptions.DefaultAgent;
                }
            }

            services.Configure<SeeingAgentOptions>(opt =>
            {
                opt.DefaultProvider = options.DefaultProvider;
                opt.DefaultModel = options.DefaultModel;
                opt.DefaultAgent = options.DefaultAgent;
                opt.Providers = options.Providers;
                opt.Models = options.Models;
                opt.ModelScope = options.ModelScope;
                opt.Agents = options.Agents;
                opt.Skills = options.Skills;
                opt.Permission = options.Permission;
                opt.Plugins = options.Plugins;
                opt.PluginEnabled = options.PluginEnabled;
            });

            // 注册核心接口和实现
            RegisterCoreServices(services);

            // 注册 LLM 服务
            RegisterLlmServices(services);

            // 注册 HttpClient 工厂
            services.AddHttpClient();

            return services;
        }

        /// <summary>
        /// 从用户配置文件加载配置（~/.seeing/seeing.json + 项目级）
        /// </summary>
        private static SeeingAgentOptions LoadUserConfiguration(ILogger? logger = null)
        {
            var options = new SeeingAgentOptions
            {
                Models = new Dictionary<string, ModelConfig>(),
                Providers = new Dictionary<string, ProviderConfig>(),
                Agents = new Dictionary<string, AgentConfig>()
            };

            // 使用统一的 WorkspaceProvider
            var workspaceProvider = new WorkspaceProvider();

            // 用户级配置：~/.seeing/seeing.json
            var userConfigPath = Path.Combine(workspaceProvider.UserSeeingDirectory, "seeing.json");
            LoadFromFile(userConfigPath, options, "用户级", logger);

            // 项目级配置：{WorkspaceRoot}/.seeing/seeing.json
            var projectConfigPath = Path.Combine(workspaceProvider.ProjectSeeingDirectory, "seeing.json");
            LoadFromFile(projectConfigPath, options, "项目级", logger);

            return options;
        }

        /// <summary>
        /// 从文件加载配置
        /// </summary>
        private static void LoadFromFile(string path, SeeingAgentOptions options, string level, ILogger? logger = null)
        {
            if (!File.Exists(path))
                return;

            try
            {
                var json = File.ReadAllText(path);
                using var doc = System.Text.Json.JsonDocument.Parse(json, new System.Text.Json.JsonDocumentOptions
                {
                    CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });

                var root = doc.RootElement;

                // 尝试从 SeeingAgent 节读取
                if (root.TryGetProperty("SeeingAgent", out var seeingAgentSection))
                {
                    ParseOptions(seeingAgentSection, options, logger);
                }
                else
                {
                    // 直接从根节点读取
                    ParseOptions(root, options, logger);
                }

                logger?.LogDebug("已从 {Level} 配置文件加载配置: {Path}", level, path);
            }
            catch (System.Text.Json.JsonException ex)
            {
                logger?.LogWarning(ex,
                    "{Level} 配置文件格式错误，已跳过: {Path}。错误位置: {Message}",
                    level, path, ex.Message);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex,
                    "加载 {Level} 配置文件失败，已跳过: {Path}",
                    level, path);
            }
        }

        /// <summary>
        /// 解析配置选项
        /// </summary>
        private static void ParseOptions(System.Text.Json.JsonElement element, SeeingAgentOptions options, ILogger? logger = null)
        {
            // Providers
            if (element.TryGetProperty("Providers", out var providers) && providers.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                foreach (var prop in providers.EnumerateObject())
                {
                    try
                    {
                        var providerConfig = System.Text.Json.JsonSerializer.Deserialize<ProviderConfig>(prop.Value.GetRawText(), ConfigJsonOptions);
                        if (providerConfig != null)
                            options.Providers[prop.Name] = providerConfig;
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning(ex, "解析 Providers.{Name} 失败，已跳过", prop.Name);
                    }
                }
            }

            // Models
            if (element.TryGetProperty("Models", out var models) && models.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                foreach (var prop in models.EnumerateObject())
                {
                    try
                    {
                        var modelConfig = System.Text.Json.JsonSerializer.Deserialize<ModelConfig>(prop.Value.GetRawText(), ConfigJsonOptions);
                        if (modelConfig != null)
                            options.Models![prop.Name] = modelConfig;
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning(ex, "解析 Models.{Name} 失败，已跳过", prop.Name);
                    }
                }
            }

            // DefaultProvider
            if (element.TryGetProperty("DefaultProvider", out var defaultProvider) && defaultProvider.ValueKind == System.Text.Json.JsonValueKind.String)
                options.DefaultProvider = defaultProvider.GetString();

            // DefaultModel
            if (element.TryGetProperty("DefaultModel", out var defaultModel) && defaultModel.ValueKind == System.Text.Json.JsonValueKind.String)
                options.DefaultModel = defaultModel.GetString();

            // DefaultAgent
            if (element.TryGetProperty("DefaultAgent", out var defaultAgent) && defaultAgent.ValueKind == System.Text.Json.JsonValueKind.String)
                options.DefaultAgent = defaultAgent.GetString();

            // Agents
            if (element.TryGetProperty("Agents", out var agents) && agents.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                foreach (var prop in agents.EnumerateObject())
                {
                    try
                    {
                        var agentConfig = System.Text.Json.JsonSerializer.Deserialize<AgentConfig>(prop.Value.GetRawText(), ConfigJsonOptions);
                        if (agentConfig != null)
                            options.Agents[prop.Name] = agentConfig;
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning(ex, "解析 Agents.{Name} 失败，已跳过", prop.Name);
                    }
                }
            }
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

            // 权限服务（新系统 - 统一权限检查入口）
            services.AddPermissionService();

            // Hook 管理器
            services.AddSingleton<HookManager>();
            services.AddSingleton<Seeing.Agent.Core.Hooks.IHookManager>(sp => sp.GetRequiredService<HookManager>());
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

            // Agent 注册表（协调者）
            services.AddSingleton<AgentRegistry>(sp =>
            {
                var logger = sp.GetRequiredService<ILogger<AgentRegistry>>();
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

            // 会话管理器 - 使用 Seeing.Session 包的实现
            services.AddSingleton<Seeing.Session.Management.SessionManager>();
            services.AddSingleton<Seeing.Session.Core.ISessionManager>(sp => sp.GetRequiredService<Seeing.Session.Management.SessionManager>());

            // 新增 DI 注册：会话事件发布器与会话生命周期管理
            services.AddSingleton<ISessionEventPublisher, SessionEventPublisher>();
            services.AddSingleton<ISessionLifecycle, SessionLifecycle>();

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
                var hookManager = sp.GetRequiredService<Seeing.Agent.Core.Hooks.IHookManager>();
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
                var toolInvoker = sp.GetRequiredService<ToolInvoker>();
                var hookManager = sp.GetRequiredService<Seeing.Agent.Core.Hooks.IHookManager>();
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
            services.AddSingleton<IWorkspaceProvider, WorkspaceProvider>();

            // 6. MCP 配置持久化服务
            services.AddSingleton<IMcpConfigPersistence, McpConfigPersistence>();

            // 7. MCP 客户端管理器（实现 IMcpManager 接口）
            services.AddSingleton<IMcpManager, McpClientManager>();
            services.AddSingleton<McpClientManager>(sp => (McpClientManager)sp.GetRequiredService<IMcpManager>());

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

            // 标题生成服务（实现 IHookHandler，自动注册到 HookManager）
            services.AddSingleton<Seeing.Agent.Services.TitleGenerationService>(sp =>
            {
                var agentExecutor = sp.GetRequiredService<AgentExecutor>();
                var agentRegistry = sp.GetRequiredService<IAgentRegistry>();
                var sessionManager = sp.GetRequiredService<Seeing.Session.Core.ISessionManager>();
                var logger = sp.GetService<ILogger<Seeing.Agent.Services.TitleGenerationService>>();

                var service = new Seeing.Agent.Services.TitleGenerationService(
                    agentExecutor,
                    agentRegistry,
                    sessionManager,
                    logger);

                // 自动注册到 HookManager
                var hookManager = sp.GetRequiredService<Seeing.Agent.Core.Hooks.HookManager>();
                hookManager.Register(service);

                return service;
            });
            services.AddSingleton<Seeing.Session.Management.ITitleGenerationService>(sp =>
                sp.GetRequiredService<Seeing.Agent.Services.TitleGenerationService>());

            // Shell 环境服务（触发 shell.env Hook）
            services.AddSingleton<IShellEnvironmentService, ShellEnvironmentService>();

            // Shell 服务（跨平台 Shell 选择和进程管理）
            services.AddSingleton<IShellService, ShellService>();

            // 命令执行服务（触发 command.execute.before Hook）
            services.AddSingleton<ICommandService, CommandService>();

            // 后台任务管理器
            services.AddSingleton<BackgroundTaskManager>();
            services.AddSingleton<IBackgroundTaskManager>(sp => sp.GetRequiredService<BackgroundTaskManager>());

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

            // LLM 服务
            services.AddSingleton<ILlmService, LlmService>();
        }

        /// <summary>
        /// 添加提示词构建服务
        /// </summary>
        public static IServiceCollection AddPromptBuilder(this IServiceCollection services)
        {
            services.AddSingleton<IInstructionLoader, InstructionLoader>();
            services.AddSingleton<SystemPromptProvider>();
            services.AddSingleton<PromptBuilder>();

            return services;
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
        /// <param name="workspaceRoot">工作区根目录</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>各组件加载结果</returns>
        public static async Task<IReadOnlyList<ComponentLoadResult>> InitializeSeeingAgentAsync(
            this IServiceProvider services,
            string workspaceRoot,
            CancellationToken cancellationToken = default)
        {
            var componentManager = services.GetRequiredService<IComponentManager>();
            return await componentManager.LoadAllAsync(workspaceRoot, cancellationToken);
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
