using Microsoft.Extensions.Logging;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Core.Permission;

namespace Seeing.Agent.Core
{
    /// <summary>
    /// Agent 注册表 - 协调者模式，委托给 Store 和 RuntimeManager
    /// <para>
    /// 参考 opencode 的 Agent.Service 设计，实现：
    /// - 内置代理注册（build, explore, plan 等）
    /// - 配置扩展代理加载
    /// - 权限筛选和代理发现
    /// - 默认代理管理
    /// </para>
    /// <para>
    /// 架构分层：
    /// - AgentStore: 纯存储操作
    /// - AgentRuntimeManager: 运行时设置管理
    /// - AgentRegistry: 业务协调层（筛选、权限、实例创建）
    /// </para>
    /// </summary>
    public class AgentRegistry : IAgentRegistry
    {
        private readonly ILogger<AgentRegistry> _logger;
        private readonly IAgentStore _agentStore;
        private readonly IAgentRuntimeManager _runtimeManager;
        private readonly string? _configDefaultAgentName;

        /// <summary>
        /// 创建 Agent 注册表实例（协调者模式）
        /// </summary>
        public AgentRegistry(
            ILogger<AgentRegistry> logger,
            IAgentStore agentStore,
            IAgentRuntimeManager runtimeManager,
            IEnumerable<AgentInfo>? builtInAgents = null,
            string? defaultAgent = null)
        {
            _logger = logger;
            _agentStore = agentStore;
            _runtimeManager = runtimeManager;
            _configDefaultAgentName = defaultAgent;

            // 注册内置代理（委托给 Store）
            if (builtInAgents != null)
            {
                foreach (var agent in builtInAgents)
                {
                    _agentStore.RegisterAsync(agent).Wait();
                }
            }

            _logger.LogInformation("AgentRegistry 初始化完成，已注册 {Count} 个代理",
                _agentStore.GetAllAsync().Result.Count);
        }

        /// <summary>
        /// 初始化运行时设置（异步加载） - 委托给 RuntimeManager
        /// </summary>
        public async Task InitializeAsync()
        {
            await _runtimeManager.InitializeAsync();
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<AgentInfo>> GetAgentsAsync()
        {
            return await _agentStore.GetAllAsync();
        }

        /// <inheritdoc/>
        public async Task<AgentInfo?> GetAgentAsync(string name)
        {
            return await _agentStore.GetAsync(name);
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<AgentInfo>> GetSubAgentsAsync()
        {
            var allAgents = await _agentStore.GetAllAsync();
            var subAgents = allAgents
                .Where(a => a.Mode == AgentMode.SubAgent)
                .OrderBy(a => a.Name, StringComparer.Ordinal)
                .ToList();
            return subAgents.AsReadOnly();
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<AgentInfo>> GetPrimaryAgentsAsync()
        {
            var allAgents = await _agentStore.GetAllAsync();
            // AgentMode.All 模式的代理也会出现在主代理列表中（可作为主代理或子代理）
            var primaryAgents = allAgents
                .Where(a => (a.Mode == AgentMode.Primary || a.Mode == AgentMode.All) && !a.IsHidden)
                .OrderBy(a => a.Name, StringComparer.Ordinal)
                .ToList();
            return primaryAgents.AsReadOnly();
        }

        /// <inheritdoc/>
        public async Task<string> GetDefaultAgentNameAsync()
        {
            // 1. 优先检查运行时设置（委托给 RuntimeManager）
            var runtimeDefault = await _runtimeManager.GetDefaultAgentNameAsync();
            if (runtimeDefault != null)
            {
                var agent = await GetAgentAsync(runtimeDefault);
                // AgentMode.All 模式的代理也可以作为主代理
                if (agent != null && (agent.Mode == AgentMode.Primary || agent.Mode == AgentMode.All) && !agent.IsHidden)
                {
                    return runtimeDefault;
                }
            }

            // 2. 检查配置文件默认
            if (_configDefaultAgentName != null)
            {
                var agent = await GetAgentAsync(_configDefaultAgentName);
                // AgentMode.All 模式的代理也可以作为主代理
                if (agent != null && (agent.Mode == AgentMode.Primary || agent.Mode == AgentMode.All) && !agent.IsHidden)
                {
                    return _configDefaultAgentName;
                }
            }

            // 3. 优先查找 "build" Agent（默认主代理）
            var buildAgent = await GetAgentAsync("build");
            if (buildAgent != null && !buildAgent.IsHidden)
            {
                return "build";
            }

            // 4. 回退：查找第一个可见的主代理（AgentMode.All 或 Primary）
            var primaryAgents = await GetPrimaryAgentsAsync();
            if (primaryAgents.Count > 0)
            {
                return primaryAgents[0].Name;
            }

            throw new InvalidOperationException("未找到可用的主代理");
        }

        /// <inheritdoc/>
        public async Task SetDefaultAgentAsync(string name)
        {
            // 委托给 RuntimeManager，它会验证代理有效性
            await _runtimeManager.SetDefaultAgentAsync(name);
        }

        /// <inheritdoc/>
        public async Task UpdateAgentModelAsync(string agentName, ModelReference model)
        {
            // 委托给 RuntimeManager，它会更新 Store 中的代理模型
            await _runtimeManager.UpdateAgentModelAsync(agentName, model);
        }

        /// <inheritdoc/>
        public async Task<ModelReference?> GetEffectiveModelAsync(string agentName)
        {
            // 委托给 RuntimeManager
            return await _runtimeManager.GetEffectiveModelAsync(agentName);
        }

        /// <inheritdoc/>
        public async Task RegisterAgentAsync(AgentInfo agentInfo)
        {
            // 委托给 Store
            await _agentStore.RegisterAsync(agentInfo);
            _logger.LogInformation("注册代理: {Name}, Mode: {Mode}", agentInfo.Name, agentInfo.Mode);
        }

        /// <inheritdoc/>
        public bool UnregisterAgent(string name)
        {
            // 委托给 Store
            var removed = _agentStore.Unregister(name);
            if (removed)
            {
                _logger.LogInformation("注销代理: {Name}", name);
            }
            return removed;
        }

        /// <inheritdoc/>
        public bool HasAgent(string name)
        {
            // 委托给 Store
            return _agentStore.Has(name);
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<AgentInfo>> GetAccessibleSubAgentsAsync(
            IReadOnlyList<PermissionRuleEntry> callerPermissions)
        {
            var subAgents = await GetSubAgentsAsync();

            // 根据权限筛选可访问的子代理
            var accessibleAgents = new List<AgentInfo>();
            foreach (var agent in subAgents)
            {
                // 检查调用者权限中是否有对 Agent 类型的 Deny 规则
                var hasDeny = callerPermissions.Any(r =>
                    r.Kind == PermissionKind.Agent &&
                    (r.Pattern == "*" || WildcardMatches(r.Pattern, agent.Name)) &&
                    r.Effect == PermissionEffect.Deny);

                if (!hasDeny)
                {
                    accessibleAgents.Add(agent);
                }
            }

            return accessibleAgents.OrderBy(a => a.Name).ToList().AsReadOnly();
        }

        /// <summary>
        /// 通配符匹配
        /// </summary>
        private static bool WildcardMatches(string pattern, string input)
        {
            if (pattern == "*") return true;
            if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(input)) return false;

            // 简单通配符匹配
            if (pattern.StartsWith("*") && pattern.EndsWith("*"))
            {
                return input.Contains(pattern[1..^1], StringComparison.OrdinalIgnoreCase);
            }
            if (pattern.StartsWith("*"))
            {
                return input.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase);
            }
            if (pattern.EndsWith("*"))
            {
                return input.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase);
            }
            return string.Equals(pattern, input, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 获取或创建 Agent 实例（用于执行）
        /// </summary>
        public IAgent? GetOrCreateAgentInstance(string name)
        {
            var info = _agentStore.GetAsync(name).Result;
            if (info == null)
                return null;

            // 如果有工厂函数，创建实例
            if (info.AgentFactory != null)
            {
                return info.AgentFactory();
            }

            // 返回一个基于信息的简单代理包装
            return new AgentInfoWrapper(info);
        }

        /// <summary>
        /// 从配置扩展代理（使用 AgentConfig 类型）
        /// </summary>
        public void ExtendFromConfig(Dictionary<string, AgentConfig> configAgents)
        {
            if (configAgents == null)
                return;

            foreach (var (key, config) in configAgents)
            {
                // 检查是否禁用
                if (config.Disable)
                {
                    UnregisterAgent(key);
                    continue;
                }

                // 更新或创建代理
                var existing = _agentStore.GetAsync(key).Result;
                if (existing == null)
                {
                    // 创建新代理
                    existing = new AgentInfo
                    {
                        Name = key,
                        Mode = AgentMode.All,
                        IsNative = false,
                        PermissionRules = GetDefaultPermissions()
                    };
                }

                // 应用配置
                if (!string.IsNullOrEmpty(config.Model))
                {
                    var provider = config.Provider ?? "openai";
                    existing.Model = new ModelReference
                    {
                        ProviderId = provider,
                        ModelId = config.Model
                    };
                }
                existing.Variant = config.Variant ?? existing.Variant;
                existing.SystemPrompt = config.SystemPrompt ?? existing.SystemPrompt;
                existing.Description = config.Description ?? existing.Description;
                existing.MaxSteps = config.MaxSteps ?? existing.MaxSteps;
                existing.Temperature = config.Temperature ?? existing.Temperature;
                existing.TopP = config.TopP ?? existing.TopP;
                existing.Mode = config.Mode ?? existing.Mode;
                existing.Color = config.Color ?? existing.Color;
                existing.IsHidden = config.IsHidden ?? existing.IsHidden;

                // 合并权限规则（新格式）
                if (config.PermissionRules != null && config.PermissionRules.Count > 0)
                {
                    var mergedRules = new List<PermissionRuleEntry>(existing.PermissionRules);
                    mergedRules.AddRange(config.PermissionRules);
                    existing.PermissionRules = mergedRules;
                }

                // 应用选项
                if (config.Options != null)
                {
                    foreach (var (optionKey, optionValue) in config.Options)
                    {
                        existing.Options[optionKey] = optionValue;
                    }
                }

                _agentStore.RegisterAsync(existing).Wait();
            }

            _logger.LogInformation("从配置扩展代理完成，当前共 {Count} 个代理",
                _agentStore.GetAllAsync().Result.Count);
        }

        /// <summary>
        /// 获取默认权限集
        /// </summary>
        private List<PermissionRuleEntry> GetDefaultPermissions()
        {
            return new List<PermissionRuleEntry>
            {
                PermissionRuleEntry.Allow(PermissionKind.Tool, "*", 0),
            };
        }

        /// <summary>
        /// 解析模型引用字符串
        /// </summary>
        private ModelReference? ParseModelReference(string? modelStr)
        {
            if (string.IsNullOrEmpty(modelStr))
                return null;

            // 格式：provider:model 或 provider/model
            var parts = modelStr.Split(new[] { ':', '/' }, 2);
            if (parts.Length >= 2)
            {
                return new ModelReference
                {
                    ProviderId = parts[0],
                    ModelId = parts[1]
                };
            }

            // 只有模型 ID，无 Provider
            return new ModelReference
            {
                ProviderId = string.Empty,
                ModelId = parts[0]
            };
        }

        /// <summary>
        /// 代理信息包装器 - 将 AgentInfo 包装为 IAgent
        /// </summary>
        private class AgentInfoWrapper : IAgent
        {
            private readonly AgentInfo _info;

            public AgentInfoWrapper(AgentInfo info)
            {
                _info = info;
            }

            public string Name { get => _info.Name; set => _info.Name = value; }
            public AgentMode Mode { get => _info.Mode; set => _info.Mode = value; }
            public string Description { get => _info.Description ?? string.Empty; set => _info.Description = value; }
            public IReadOnlyList<PermissionRuleEntry> PermissionRules { get => _info.PermissionRules.AsReadOnly(); set { _info.PermissionRules.Clear(); _info.PermissionRules.AddRange(value); } }
            public string? SystemPrompt { get => _info.SystemPrompt; set => _info.SystemPrompt = value; }
            public ModelReference? Model { get => _info.Model; set => _info.Model = value; }
            public int? MaxSteps { get => _info.MaxSteps; set => _info.MaxSteps = value; }
            public AgentStatus Status { get => _info.AgentFactory != null ? AgentStatus.Ready : AgentStatus.RequiresFactory; set { } }
            public IReadOnlyList<string> AllowedTools { get => _info.AllowedTools.AsReadOnly(); set { _info.AllowedTools.Clear(); _info.AllowedTools.AddRange(value); } }
            public IReadOnlyList<string> DeniedTools { get => _info.DeniedTools.AsReadOnly(); set { _info.DeniedTools.Clear(); _info.DeniedTools.AddRange(value); } }
            public PermissionEffect PermissionDefaultEffect { get => _info.PermissionDefaultEffect; set => _info.PermissionDefaultEffect = value; }

            public async IAsyncEnumerable<ChatMessage> ExecuteAsync(
                ChatMessage input,
                AgentContext context,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                // 基础包装器不执行实际逻辑，返回提示消息
                yield return new ChatMessage
                {
                    Role = "system",
                    Content = $"代理 {_info.Name} 需要通过 AgentFactory 创建实例才能执行"
                };
            }
        }
    }
}