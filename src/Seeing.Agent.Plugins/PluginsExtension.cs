using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Plugins.Agents;

namespace Seeing.Agent.Plugins
{
    /// <summary>
    /// Seeing.Agent.Plugins 扩展入口点
    /// <para>
    /// 提供以下 Agent：
    /// - Sisyphus：主工作代理，负责执行复杂任务和编排子代理
    /// - Oracle：只读咨询代理，提供架构评审和决策建议
    /// - Explore：代码库探索代理，用于搜索和理解代码
    /// - Librarian：参考查找代理，用于搜索外部文档和示例
    /// - Metis：预规划顾问，分析请求识别隐藏意图
    /// - Momus：计划评审代理，评估工作计划的完整性
    /// - Prometheus：计划生成器，创建详细的实现计划
    /// - Hephaestus：后台实现代理，用于并行工作
    /// - Atlas：大模型协调器，处理模型切换和负载均衡
    /// - MultimodalLooker：多模态分析代理
    /// - SisyphusJunior：轻量级工作代理
    /// </para>
    /// </summary>
    public class PluginsExtension : IExtension
    {
        /// <summary>扩展 ID</summary>
        public string? Id => "seeing.agent.plugins";

        /// <summary>版本号</summary>
        public string Version => "1.0.0";

        /// <summary>显示名称</summary>
        public string Name => "Seeing.Agent Plugins";

        /// <summary>描述</summary>
        public string Description => "内置 Agent 实现，包含 Sisyphus、Oracle、Explore、Librarian 等代理";

        /// <summary>目标运行时</summary>
        public string Target => "server";

        private readonly List<IAgent> _agents = new();
        private ILogger? _logger;

        /// <summary>
        /// 注册服务
        /// </summary>
        public void ConfigureServices(IServiceCollection services)
        {
            // 注册 Agent 依赖的服务
            services.AddSingleton<AgentDependencyResolver>();
        }

        /// <summary>
        /// 初始化扩展
        /// </summary>
        public async Task InitializeAsync(ExtensionContext context, ExtensionMeta meta)
        {
            // 插件在独立 AssemblyLoadContext 中加载时，DI 无法解析 ILogger<PluginsExtension>
            var loggerFactory = context.Services.GetRequiredService<ILoggerFactory>();
            _logger = loggerFactory.CreateLogger<PluginsExtension>();
            _logger.LogInformation("初始化 {Name} v{Version} (state: {State})",
                Name, Version, meta.State);

            var hookManager = context.HookManager;

            // 创建所有 Agent 实例
            _agents.AddRange(CreateAgents(loggerFactory, hookManager));

            _logger.LogInformation("已注册 {Count} 个 Agent: {Agents}",
                _agents.Count, string.Join(", ", _agents.Select(a => a.Name)));

            await Task.CompletedTask;
        }

        /// <summary>
        /// 获取提供的 Agent
        /// </summary>
        public IEnumerable<IAgent> GetAgents() => _agents;

        /// <summary>
        /// 清理资源
        /// </summary>
        public async Task DisposeAsync()
        {
            _logger?.LogInformation("清理 {Name}", Name);
            _agents.Clear();
            await Task.CompletedTask;
        }

        /// <summary>
        /// 创建所有 Agent 实例
        /// </summary>
        private static IEnumerable<IAgent> CreateAgents(
            ILoggerFactory loggerFactory,
            HookManager? hookManager)
        {
            // Primary Agents - Sisyphus 需要 HookManager
            yield return new SisyphusAgent(
                loggerFactory.CreateLogger<SisyphusAgent>(),
                hookManager!);

            // Sub Agents
            yield return new OracleAgent(loggerFactory.CreateLogger<OracleAgent>());
            yield return new ExploreAgent(loggerFactory.CreateLogger<ExploreAgent>());
            yield return new LibrarianAgent(loggerFactory.CreateLogger<LibrarianAgent>());
            yield return new MetisAgent(loggerFactory.CreateLogger<MetisAgent>());
            yield return new MomusAgent(loggerFactory.CreateLogger<MomusAgent>());
            yield return new PrometheusAgent(loggerFactory.CreateLogger<PrometheusAgent>());
            yield return new HephaestusAgent(loggerFactory.CreateLogger<HephaestusAgent>());
            yield return new AtlasAgent(loggerFactory.CreateLogger<AtlasAgent>());
            yield return new MultimodalLookerAgent(loggerFactory.CreateLogger<MultimodalLookerAgent>());
            yield return new SisyphusJuniorAgent(loggerFactory.CreateLogger<SisyphusJuniorAgent>());
        }
    }

    /// <summary>
    /// Agent 依赖解析器（预留扩展点）
    /// </summary>
    public class AgentDependencyResolver
    {
        // 可在此添加 Agent 共享的依赖服务
    }
}