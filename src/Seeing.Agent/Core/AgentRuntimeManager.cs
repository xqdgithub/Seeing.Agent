using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Llm;

namespace Seeing.Agent.Core
{
    /// <summary>
    /// Agent 运行时管理实现 - 管理运行时设置和模型配置
    /// <para>
    /// 负责加载和应用运行时设置，包括：
    /// - 默认代理设置
    /// - Agent 特定的模型配置
    /// - 配置持久化
    /// - 模型变更事件广播
    /// - 会话级模型状态管理
    /// </para>
    /// <para>
    /// 模型优先级（从高到低）：
    /// 1. 会话级用户设置（SessionModelOverrides）- 仅当前会话有效
    /// 2. 持久化的运行时设置（RuntimeSettings.AgentModels）
    /// 3. Agent 代码/配置定义的模型
    /// 4. 上次使用的模型（LastUsedModel）
    /// 5. 全局默认模型（SeeingAgentOptions.DefaultModel）
    /// </para>
    /// </summary>
    public class AgentRuntimeManager : IAgentRuntimeManager
    {
        private readonly ILogger<AgentRuntimeManager> _logger;
        private readonly IConfigurationPersistence? _persistence;
        private readonly IConfigReloadService? _reloadService;
        private RuntimeSettings? _runtimeSettings;
        private readonly IAgentStore _agentStore;
        private readonly SeeingAgentOptions _options;
        private readonly ILlmService? _llmService;

        /// <summary>模型变更事件 - 当 Agent 的模型配置发生变更时触发</summary>
        public event EventHandler<AgentModelChangedEventArgs>? ModelChanged;

        // ========== 会话级状态（不持久化）==========

        /// <summary>
        /// 会话级模型覆盖（用户在当前会话中手动设置的模型）
        /// <para>Key: Agent 名称, Value: 模型 ID</para>
        /// </summary>
        private readonly Dictionary<string, string> _sessionModelOverrides = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 上次使用的模型（用于切换 Agent 时回退）
        /// </summary>
        private string? _lastUsedModel;

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
            IOptions<SeeingAgentOptions>? options = null,
            IConfigurationPersistence? persistence = null,
            IConfigReloadService? reloadService = null,
            ILlmService? llmService = null)
        {
            _logger = logger;
            _agentStore = agentStore;
            _options = options?.Value ?? new SeeingAgentOptions();
            _persistence = persistence;
            _reloadService = reloadService;
            _llmService = llmService;
        }

        /// <summary>
        /// 订阅配置热重载服务
        /// </summary>
        public void SubscribeToConfigReload()
        {
            if (_reloadService != null)
            {
                _reloadService.ConfigChanged += OnConfigChanged;
                _logger.LogInformation("已订阅配置热重载服务");
            }
        }

        /// <summary>
        /// 取消订阅配置热重载服务
        /// </summary>
        public void UnsubscribeFromConfigReload()
        {
            if (_reloadService != null)
            {
                _reloadService.ConfigChanged -= OnConfigChanged;
                _logger.LogInformation("已取消订阅配置热重载服务");
            }
        }

        /// <summary>
        /// 配置变更处理
        /// </summary>
        private async void OnConfigChanged(object? sender, ConfigReloadEventArgs e)
        {
            try
            {
                if (e.NewSettings == null)
                    return;

                switch (e.ChangeType)
                {
                    case ConfigChangeType.AgentModelChanged:
                        await HandleAgentModelChangeAsync(e);
                        break;

                    case ConfigChangeType.DefaultAgentChanged:
                        _runtimeSettings = e.NewSettings;
                        _logger.LogInformation("默认代理已变更: {Agent}", e.NewSettings.DefaultAgent);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理配置变更失败");
            }
        }

        /// <summary>
        /// 处理 Agent 模型变更
        /// </summary>
        private async Task HandleAgentModelChangeAsync(ConfigReloadEventArgs e)
        {
            if (e.NewSettings?.AgentModels == null || string.IsNullOrEmpty(e.Section))
                return;

            // 解析 Agent 名称（Section 格式为 "AgentModels.{agentName}"）
            var agentName = e.Section.StartsWith("AgentModels.") 
                ? e.Section.Substring("AgentModels.".Length) 
                : e.Section;

            var agent = await _agentStore.GetAsync(agentName);
            if (agent == null)
            {
                _logger.LogWarning("配置变更的 Agent 不存在: {Agent}", agentName);
                return;
            }

            // 获取旧模型
            var oldModel = agent.Model;

            // 更新运行时设置
            _runtimeSettings = e.NewSettings;

            // 应用新模型
            ApplyRuntimeModel(agent);

            // 触发事件
            OnModelChanged(new AgentModelChangedEventArgs
            {
                AgentName = agentName,
                OldModel = oldModel,
                NewModel = agent.Model,
                Source = ModelChangeSource.ConfigReload
            });

            _logger.LogInformation("Agent 模型已通过热重载更新: {Agent} -> {Model}", 
                agentName, agent.Model?.ToString() ?? "null");
        }

        /// <inheritdoc/>
        public async Task InitializeAsync()
        {
            if (_persistence != null)
            {
                _runtimeSettings = await _persistence.LoadAsync();
                _logger.LogInformation("已加载运行时设置: DefaultAgent={Agent}", _runtimeSettings.DefaultAgent);

                // 应用运行时设置中的 Agent 模型配置
                if (_runtimeSettings.AgentModels != null)
                {
                    var allAgents = await _agentStore.GetAllAsync();
                    foreach (var agent in allAgents)
                    {
                        ApplyRuntimeModel(agent);
                    }
                }
            }

            // 订阅配置热重载
            SubscribeToConfigReload();
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

            // 更新运行时设置
            _runtimeSettings ??= new RuntimeSettings();
            _runtimeSettings.DefaultAgent = agentName;

            // 持久化
            if (_persistence != null)
            {
                await _persistence.SaveAsync(_runtimeSettings);
                _logger.LogInformation("已设置默认代理: {Name}", agentName);
            }
            else
            {
                _logger.LogWarning("未配置持久化服务，设置仅对当前会话有效");
            }
        }

        /// <inheritdoc/>
        public async Task<string?> GetDefaultAgentNameAsync()
        {
            return _runtimeSettings?.DefaultAgent;
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

            // 更新运行时设置
            _runtimeSettings ??= new RuntimeSettings();
            var modelId = string.IsNullOrEmpty(model.ProviderId)
                ? model.ModelId
                : $"{model.ProviderId}/{model.ModelId}";
            _runtimeSettings.SetAgentModel(agentName, modelId);

            // 持久化
            if (_persistence != null)
            {
                await _persistence.SaveAsync(_runtimeSettings);
                _logger.LogInformation("已更新代理模型: {Agent} -> {Model}", agentName, modelId);
            }
            else
            {
                _logger.LogWarning("未配置持久化服务，设置仅对当前会话有效");
            }

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
        /// 获取有效模型 ID（内部方法，实现完整优先级逻辑）
        /// </summary>
        private async Task<string?> GetEffectiveModelIdAsync(string agentName)
        {
            if (string.IsNullOrEmpty(agentName))
                return null;

            // 1. 会话级用户设置（最高优先级）
            if (_sessionModelOverrides.TryGetValue(agentName, out var sessionModel))
            {
                _logger.LogDebug("[ModelManager] Agent {Agent} 使用会话级设置模型: {Model}", agentName, sessionModel);
                return sessionModel;
            }

            // 2. 持久化的运行时设置
            var agent = await _agentStore.GetAsync(agentName);
            if (agent?.Model != null)
            {
                var agentModel = agent.Model.ToString();
                _logger.LogDebug("[ModelManager] Agent {Agent} 使用配置模型: {Model}", agentName, agentModel);
                return agentModel;
            }

            // 3. 上次使用的模型
            if (!string.IsNullOrEmpty(_lastUsedModel))
            {
                _logger.LogDebug("[ModelManager] Agent {Agent} 使用上次模型: {Model}", agentName, _lastUsedModel);
                return _lastUsedModel;
            }

            // 4. 全局默认模型
            if (!string.IsNullOrEmpty(_options.DefaultModel))
            {
                _logger.LogDebug("[ModelManager] Agent {Agent} 使用全局默认模型: {Model}", agentName, _options.DefaultModel);
                return _options.DefaultModel;
            }

            _logger.LogWarning("[ModelManager] Agent {Agent} 未找到有效模型", agentName);
            return null;
        }

        /// <summary>
        /// 设置会话级模型（仅当前会话有效，不持久化）
        /// </summary>
        /// <param name="agentName">Agent 名称</param>
        /// <param name="modelId">模型 ID</param>
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
                    // 尝试带 provider 前缀匹配
                    foreach (var provider in _options.Providers.Keys)
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

            // 设置会话级覆盖
            _sessionModelOverrides[agentName] = modelId;
            CurrentModel = modelId;
            _lastUsedModel = modelId;

            _logger.LogInformation("[ModelManager] 会话级模型设置: {Agent} -> {Model}", agentName, modelId);
        }

        /// <summary>
        /// 切换 Agent 并返回有效模型
        /// <para>
        /// 模型优先级：
        /// 1. 该 Agent 的会话级设置
        /// 2. Agent 配置的模型
        /// 3. 上次使用的模型
        /// 4. 全局默认模型
        /// </para>
        /// </summary>
        /// <param name="newAgentName">新 Agent 名称</param>
        /// <returns>有效模型引用</returns>
        public async Task<ModelReference?> SwitchAgentAsync(string newAgentName)
        {
            if (string.IsNullOrEmpty(newAgentName))
                throw new ArgumentException("Agent 名称不能为空", nameof(newAgentName));

            var previousAgent = CurrentAgentName;
            CurrentAgentName = newAgentName;

            // 获取新 Agent 的有效模型
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

        /// <summary>
        /// 清除会话级模型设置
        /// </summary>
        /// <param name="agentName">Agent 名称，为 null 则清除所有</param>
        public void ClearSessionModelOverride(string? agentName = null)
        {
            if (string.IsNullOrEmpty(agentName))
            {
                _sessionModelOverrides.Clear();
                _logger.LogInformation("[ModelManager] 已清除所有会话级模型设置");
            }
            else if (_sessionModelOverrides.Remove(agentName))
            {
                _logger.LogInformation("[ModelManager] 已清除 Agent {Agent} 的会话级模型设置", agentName);
            }
        }

        /// <inheritdoc/>
        public void ApplyRuntimeModel(AgentInfo agent)
        {
            if (_runtimeSettings?.AgentModels == null)
                return;

            var modelId = _runtimeSettings.GetAgentModel(agent.Name);
            if (modelId != null)
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