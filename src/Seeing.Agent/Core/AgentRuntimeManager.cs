using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Llm;
using System.Collections.Concurrent;

namespace Seeing.Agent.Core
{
    /// <summary>
    /// Agent 运行时管理实现 - 管理运行时设置和模型配置
    /// <para>所有持久化配置统一走 UnifiedConfigManager → seeing.json，不再依赖独立 settings.json。</para>
    /// <para>
    /// 有效模型解析委托 <see cref="IModelManager.ResolveNativeModel"/>（会话 UI 覆盖作为 sessionModelRef）。
    /// <see cref="_lastUsedModel"/> 仅用于 <see cref="CurrentModel"/> 展示，不参与解析。
    /// </para>
    /// </summary>
    public class AgentRuntimeManager : IAgentRuntimeManager
    {
        private readonly ILogger<AgentRuntimeManager> _logger;
        private readonly IAgentStore _agentStore;
        private readonly IOptionsMonitor<SeeingAgentOptions> _optionsMonitor;
        private readonly IConfigSectionStore _configStore;
        private readonly IModelManager _modelManager;
        private readonly IProviderRegistry _providerRegistry;
        private readonly ILlmService? _llmService;

        /// <summary>模型变更事件 - 当 Agent 的模型配置发生变更时触发</summary>
        public event EventHandler<AgentModelChangedEventArgs>? ModelChanged;

        // ========== 会话级状态（不持久化）==========

        /// <summary>
        /// 会话级模型覆盖（用户在当前会话中手动设置的模型）
        /// <para>Key: Agent 名称, Value: 模型 ID</para>
        /// </summary>
        private readonly ConcurrentDictionary<string, string> _sessionModelOverrides = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 上次使用的模型（用于切换 Agent 时回退）
        /// </summary>
        private string? _lastUsedModel;
        private readonly object _modelLock = new();

        /// <summary>
        /// 当前 Agent 名称
        /// </summary>
        public string CurrentAgentName { get; private set; } = "primary";

        /// <summary>
        /// 当前使用的模型
        /// </summary>
        public string? CurrentModel { get; private set; }

        /// <summary>
        /// 创建 Agent 运行时管理实例
        /// </summary>
        public AgentRuntimeManager(
            ILogger<AgentRuntimeManager> logger,
            IAgentStore agentStore,
            IOptionsMonitor<SeeingAgentOptions> optionsMonitor,
            IConfigSectionStore configStore,
            IModelManager modelManager,
            IProviderRegistry providerRegistry,
            ILlmService? llmService = null)
        {
            _logger = logger;
            _agentStore = agentStore;
            _optionsMonitor = optionsMonitor;
            _configStore = configStore;
            _modelManager = modelManager;
            _providerRegistry = providerRegistry;
            _llmService = llmService;

            // 订阅配置热重载事件
            _configStore.ConfigChanged += OnConfigChanged;
        }

        /// <inheritdoc/>
        public async Task InitializeAsync()
        {
            var options = _optionsMonitor.CurrentValue;

            // 应用 AgentModels 中的模型配置
            if (options.AgentModels is { Count: > 0 })
            {
                var allAgents = await _agentStore.GetAllAsync();
                foreach (var agent in allAgents)
                {
                    ApplyRuntimeModel(agent);
                }
            }

            _logger.LogInformation(
                "AgentRuntimeManager 已初始化: DefaultAgent={Agent}, AgentModels={Count}",
                options.DefaultAgent,
                options.AgentModels?.Count ?? 0);
        }

        /// <summary>
        /// UnifiedConfigManager 配置变更处理
        /// </summary>
        private void OnConfigChanged(object? sender, ConfigChangedEventArgs e)
        {
            if (e.ChangedSections.Length == 0)
                return;

            // 关注 DefaultAgent 和 AgentModels 变更
            foreach (var section in e.ChangedSections)
            {
                if (section is "DefaultAgent")
                {
                    _logger.LogInformation("[AgentRuntimeManager] 默认 Agent 已变更: {Agent}",
                        _optionsMonitor.CurrentValue.DefaultAgent);
                }
                else if (section is "AgentModels")
                {
                    _logger.LogInformation("[AgentRuntimeManager] AgentModels 已更新，重新应用模型配置");
                    _ = ApplyAgentModelsAsync();
                }
            }
        }

        private async Task ApplyAgentModelsAsync()
        {
            var options = _optionsMonitor.CurrentValue;
            if (options.AgentModels is not { Count: > 0 }) return;

            var allAgents = await _agentStore.GetAllAsync();
            foreach (var agent in allAgents)
            {
                ApplyRuntimeModel(agent);
            }
        }

        /// <inheritdoc/>
        public async Task SetDefaultAgentAsync(string agentName)
        {
            if (string.IsNullOrEmpty(agentName))
                throw new ArgumentException("Agent 名称不能为空", nameof(agentName));

            var agent = await _agentStore.GetAsync(agentName);
            if (agent == null)
                throw new ArgumentException($"Agent 不存在: {agentName}", nameof(agentName));

            if ((agent.Mode != AgentMode.Primary && agent.Mode != AgentMode.All) || agent.IsHidden)
                throw new ArgumentException($"Agent 不是可见的主代理: {agentName}", nameof(agentName));

            // 默认保存到项目级（AgentsPage 层会透传 ConfigLevel）
            await _configStore.SaveSectionAsync("DefaultAgent", agentName, ConfigLevel.Project);
            _logger.LogInformation("已设置默认 Agent: {Name}", agentName);
        }

        /// <inheritdoc/>
        public async Task SetDefaultAgentAsync(string agentName, ConfigLevel level)
        {
            if (string.IsNullOrEmpty(agentName))
                throw new ArgumentException("Agent 名称不能为空", nameof(agentName));

            var agent = await _agentStore.GetAsync(agentName);
            if (agent == null)
                throw new ArgumentException($"Agent 不存在: {agentName}", nameof(agentName));

            if ((agent.Mode != AgentMode.Primary && agent.Mode != AgentMode.All) || agent.IsHidden)
                throw new ArgumentException($"Agent 不是可见的主代理: {agentName}", nameof(agentName));

            await _configStore.SaveSectionAsync("DefaultAgent", agentName, level);
            _logger.LogInformation("已设置默认 Agent: {Name} (级别: {Level})", agentName, level);
        }

        /// <inheritdoc/>
        public async Task<string?> GetDefaultAgentNameAsync()
        {
            return _optionsMonitor.CurrentValue.DefaultAgent;
        }

        /// <inheritdoc/>
        public async Task UpdateAgentModelAsync(string agentName, ModelReference model)
        {
            if (string.IsNullOrEmpty(agentName))
                throw new ArgumentException("Agent 名称不能为空", nameof(agentName));

            if (model == null)
                throw new ArgumentNullException(nameof(model));

            if (!_agentStore.Has(agentName))
                throw new ArgumentException($"Agent 不存在: {agentName}", nameof(agentName));

            // 获取旧模型
            var agent = await _agentStore.GetAsync(agentName);
            var oldModel = agent?.Model;

            // 更新内存中的 Agent 配置
            if (agent != null)
            {
                agent.Model = model;
            }

            // 持久化到 AgentModels
            var modelId = string.IsNullOrEmpty(model.ProviderId)
                ? model.ModelId
                : $"{model.ProviderId}/{model.ModelId}";

            var agentModels = new Dictionary<string, string>(
                _optionsMonitor.CurrentValue.AgentModels ?? new(),
                StringComparer.OrdinalIgnoreCase)
            {
                [agentName] = modelId
            };

            await _configStore.SaveSectionAsync("AgentModels", agentModels);
            _logger.LogInformation("已更新 Agent 模型: {Agent} -> {Model}", agentName, modelId);

            // 触发事件
            OnModelChanged(new AgentModelChangedEventArgs
            {
                AgentName = agentName,
                OldModel = oldModel,
                NewModel = model,
                Source = ModelChangeSource.Manual
            });
        }

        /// <inheritdoc/>
        public async Task<ModelReference?> GetEffectiveModelAsync(string agentName)
        {
            var effectiveModelId = await GetEffectiveModelIdAsync(agentName);
            if (string.IsNullOrEmpty(effectiveModelId))
                return null;
            return ParseModelReference(effectiveModelId);
        }

        /// <summary>
        /// 获取有效模型 ID（委托 <see cref="IModelManager.ResolveNativeModel"/>）
        /// </summary>
        private Task<string?> GetEffectiveModelIdAsync(string agentName)
        {
            if (string.IsNullOrEmpty(agentName))
                return Task.FromResult<string?>(null);

            _sessionModelOverrides.TryGetValue(agentName, out var sessionOverride);
            var sessionRef = string.IsNullOrEmpty(sessionOverride) ? null : sessionOverride;

            var effective = _modelManager.ResolveNativeModel(null, sessionRef, agentName);
            if (string.IsNullOrEmpty(effective))
            {
                _logger.LogWarning("[ModelManager] Agent {Agent} 未找到有效模型", agentName);
                return Task.FromResult<string?>(null);
            }

            _logger.LogDebug("[ModelManager] Agent {Agent} 有效模型: {Model}", agentName, effective);
            return Task.FromResult<string?>(effective);
        }

        /// <inheritdoc/>
        public async Task SetSessionModelOverrideAsync(string agentName, string modelId)
        {
            if (string.IsNullOrEmpty(agentName))
                throw new ArgumentException("Agent 名称不能为空", nameof(agentName));

            if (string.IsNullOrEmpty(modelId))
                throw new ArgumentException("模型 ID 不能为空", nameof(modelId));

            // 验证模型是否存在
            if (_llmService != null)
            {
                var modelConfig = _llmService.GetModelConfig(modelId);
                if (modelConfig == null)
                {
                    foreach (var provider in _providerRegistry.GetProviders().Keys)
                    {
                        var prefixedId = $"{provider}/{modelId}";
                        modelConfig = _llmService.GetModelConfig(prefixedId);
                        if (modelConfig != null)
                        {
                            modelId = prefixedId;
                            break;
                        }
                    }
                }

                if (modelConfig == null)
                    throw new ArgumentException($"模型不存在: {modelId}");
            }

            lock (_modelLock)
            {
                _sessionModelOverrides.AddOrUpdate(agentName, modelId, (_, _) => modelId);
                CurrentModel = modelId;
                _lastUsedModel = modelId;
            }

            _logger.LogInformation("[ModelManager] 会话级模型设置: {Agent} -> {Model}", agentName, modelId);
        }

        /// <inheritdoc/>
        public async Task<ModelReference?> SwitchAgentAsync(string newAgentName)
        {
            if (string.IsNullOrEmpty(newAgentName))
                throw new ArgumentException("Agent 名称不能为空", nameof(newAgentName));

            var previousAgent = CurrentAgentName;
            CurrentAgentName = newAgentName;

            var effectiveModelId = await GetEffectiveModelIdAsync(newAgentName);
            if (!string.IsNullOrEmpty(effectiveModelId))
            {
                CurrentModel = effectiveModelId;
                _lastUsedModel = effectiveModelId;
            }

            _logger.LogDebug(
                "[ModelManager] 切换 Agent: {Old} -> {New}, 模型: {Model}",
                previousAgent, newAgentName, effectiveModelId ?? "无");

            return string.IsNullOrEmpty(effectiveModelId) ? null : ParseModelReference(effectiveModelId);
        }

        /// <inheritdoc/>
        public void ClearSessionModelOverride(string? agentName = null)
        {
            lock (_modelLock)
            {
                if (string.IsNullOrEmpty(agentName))
                {
                    _sessionModelOverrides.Clear();
                    CurrentModel = null;
                    _lastUsedModel = null;
                    _logger.LogInformation("[ModelManager] 已清除所有会话级模型设置");
                }
                else if (_sessionModelOverrides.TryRemove(agentName, out _))
                {
                    if (CurrentModel != null && _sessionModelOverrides.IsEmpty)
                    {
                        CurrentModel = null;
                        _lastUsedModel = null;
                    }
                    _logger.LogInformation("[ModelManager] 已清除 Agent {Agent} 的会话级模型设置", agentName);
                }
            }
        }

        /// <inheritdoc/>
        public void ApplyRuntimeModel(Models.AgentDefinition agent)
        {
            var agentModels = _optionsMonitor.CurrentValue.AgentModels;
            if (agentModels == null || agentModels.Count == 0)
                return;

            if (agentModels.TryGetValue(agent.Name, out var modelId))
            {
                var modelRef = ParseModelReference(modelId);
                if (modelRef != null)
                {
                    agent.Model = modelRef;
                    _logger.LogDebug("应用运行时模型配置: {Agent} -> {Model}", agent.Name, modelId);
                }
            }
        }

        /// <summary>
        /// 解析模型引用字符串
        /// </summary>
        private ModelReference? ParseModelReference(string? modelStr)
        {
            if (string.IsNullOrEmpty(modelStr))
                return null;

            var parts = modelStr.Split(new[] { ':', '/' }, 2);
            if (parts.Length >= 2)
            {
                return new ModelReference
                {
                    ProviderId = parts[0],
                    ModelId = parts[1]
                };
            }

            return new ModelReference
            {
                ProviderId = string.Empty,
                ModelId = parts[0]
            };
        }

        /// <summary>
        /// 触发模型变更事件
        /// </summary>
        protected virtual void OnModelChanged(AgentModelChangedEventArgs e)
        {
            _logger.LogDebug("触发模型变更事件: {Agent}, {Old} -> {New}",
                e.AgentName, e.OldModel?.ToString() ?? "null", e.NewModel?.ToString() ?? "null");
            ModelChanged?.Invoke(this, e);
        }
    }
}