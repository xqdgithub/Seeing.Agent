using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Interfaces;

namespace Seeing.Agent.Core
{
    /// <summary>
    /// Agent 初始化服务 - 实现 IHostedService，在应用启动时异步初始化 Agent 系统
    /// <para>
    /// 职责：
    /// - 异步加载运行时设置
    /// - 应用持久化的模型配置
    /// - 避免在 DI 注册时阻塞
    /// </para>
    /// <para>
    /// 注意：AgentManager 自己实现 IHostedService 处理 MD 配置加载
    /// </para>
    /// </summary>
    public class AgentInitializationService : IHostedService
    {
        private readonly ILogger<AgentInitializationService> _logger;
        private readonly IAgentRuntimeManager _runtimeManager;

        /// <summary>
        /// 创建 Agent 初始化服务实例
        /// </summary>
        public AgentInitializationService(
            ILogger<AgentInitializationService> logger,
            IAgentRuntimeManager runtimeManager)
        {
            _logger = logger;
            _runtimeManager = runtimeManager;
        }

        /// <inheritdoc/>
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("开始初始化 Agent 系统...");

            try
            {
                // 初始化运行时设置（委托给 RuntimeManager）
                await _runtimeManager.InitializeAsync();

                _logger.LogInformation("Agent 系统初始化完成");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Agent 系统初始化失败");
                throw;
            }
        }

        /// <inheritdoc/>
        public Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Agent 系统关闭");
            return Task.CompletedTask;
        }
    }
}