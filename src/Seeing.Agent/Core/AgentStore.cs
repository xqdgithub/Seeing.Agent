using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Interfaces;
using System.Collections.Concurrent;

namespace Seeing.Agent.Core
{
    /// <summary>
    /// Agent 存储实现 - 纯存储操作
    /// <para>
    /// 使用 ConcurrentDictionary 管理 Agent 实例
    /// 提供基本的注册、查询、注销功能
    /// </para>
    /// </summary>
    public class AgentStore : IAgentStore
    {
        private readonly ILogger<AgentStore> _logger;
        private readonly ConcurrentDictionary<string, AgentInfo> _agents = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 创建 Agent 存储实例
        /// </summary>
        public AgentStore(ILogger<AgentStore> logger)
        {
            _logger = logger;
        }

        /// <inheritdoc/>
        public async Task RegisterAsync(AgentInfo agentInfo)
        {
            if (agentInfo == null || string.IsNullOrEmpty(agentInfo.Name))
            {
                _logger.LogWarning("尝试注册无效代理，已跳过");
                return;
            }

            if (_agents.ContainsKey(agentInfo.Name))
            {
                _logger.LogDebug("代理已存在，将被覆盖: {Name}", agentInfo.Name);
            }

            _agents[agentInfo.Name] = agentInfo;
            _logger.LogDebug("已注册代理: {Name}", agentInfo.Name);
        }

        /// <inheritdoc/>
        public bool Unregister(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;

            var removed = _agents.TryRemove(name, out _);
            if (removed)
            {
                _logger.LogDebug("已注销代理: {Name}", name);
            }
            return removed;
        }

        /// <inheritdoc/>
        public async Task<AgentInfo?> GetAsync(string name)
        {
            if (string.IsNullOrEmpty(name))
                return null;

            return _agents.TryGetValue(name, out var agent) ? agent : null;
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<AgentInfo>> GetAllAsync()
        {
            var agents = _agents.Values
                .OrderBy(a => a.Name)
                .ToList();
            return agents.AsReadOnly();
        }

        /// <inheritdoc/>
        public bool Has(string name)
        {
            return !string.IsNullOrEmpty(name) && _agents.ContainsKey(name);
        }

        /// <summary>
        /// 获取内部存储的代理数量（用于日志）
        /// </summary>
        public int Count => _agents.Count;
    }
}