using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Commands;
using Seeing.Agent.Configuration;
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
            // 权限评估器（具体类型与接口共享同一单例，避免双实例）
            services.AddSingleton<RuleEngine>();
            services.AddSingleton<IRuleEvaluator>(sp => sp.GetRequiredService<RuleEngine>());
            services.AddSingleton<IRuleEngine>(sp => sp.GetRequiredService<RuleEngine>());

            // Hook 管理器
            services.AddSingleton<HookManager>();
            services.AddSingleton<IHookManager>(sp => sp.GetRequiredService<HookManager>());

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
            // TaskTool 需要实现 IAgentRegistry，暂时跳过
            // services.AddSingleton<ITool, Tools.BuiltIn.SubTask.TaskTool>();
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

            // 执行上下文相关
            services.AddSingleton<IMetadataStore, ConcurrentMetadataStore>();
            services.AddSingleton<IPermissionChannel, DefaultPermissionChannel>();

            // Shell 环境服务（触发 shell.env Hook）
            services.AddSingleton<IShellEnvironmentService, ShellEnvironmentService>();

            // Shell 服务（跨平台 Shell 选择和进程管理）
            services.AddSingleton<IShellService, ShellService>();

            // 命令执行服务（触发 command.execute.before Hook）
            services.AddSingleton<ICommandService, CommandService>();
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
