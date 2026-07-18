using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Core.Permission;
using Seeing.Session.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Seeing.Agent.Configuration
{
    /// <summary>
    /// Agent 管理器 - 统一管理 Agent 的注册、发现、配置和运行时
    /// <para>
    /// 合并了原 IAgentRegistry 和 IAgentConfigLoader 的职责，
    /// 确保 Agent 信息和配置的一致性。
    /// </para>
    /// </summary>
    public class AgentManager : IAgentManager, IAgentRegistry, IHostedService
    {
        private readonly ILogger<AgentManager> _logger;
        private readonly IAgentStore _agentStore;
        private readonly IAgentRuntimeManager _runtimeManager;
        private readonly IWorkspaceProvider _workspaceProvider;
        private readonly IOptions<SeeingAgentOptions>? _options;
        private readonly IDeserializer _yamlDeserializer;
        private readonly ISerializer _yamlSerializer;
        private readonly ConcurrentDictionary<string, AgentConfigFile> _configCache = new();

        /// <summary>
        /// 原始 Agent 定义备份（用于 MD 删除后恢复）
        /// </summary>
        private readonly ConcurrentDictionary<string, AgentDefinition> _originalAgents = new(StringComparer.OrdinalIgnoreCase);

        /// <inheritdoc/>
        public event EventHandler<AgentConfigChangedEventArgs>? ConfigChanged;

        public AgentManager(
            ILogger<AgentManager> logger,
            IAgentStore agentStore,
            IAgentRuntimeManager runtimeManager,
            IWorkspaceProvider workspaceProvider,
            IEnumerable<AgentDefinition>? builtInAgents = null,
            string? defaultAgent = null,
            IOptions<SeeingAgentOptions>? options = null)
        {
            _logger = logger;
            _agentStore = agentStore;
            _runtimeManager = runtimeManager;
            _workspaceProvider = workspaceProvider;
            _options = options;

            _yamlDeserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();
            _yamlSerializer = new SerializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
                .Build();

            // 注册内置代理
            if (builtInAgents != null)
            {
                foreach (var agent in builtInAgents)
                {
                    _agentStore.RegisterAsync(agent).Wait();
                }
            }

            // 备份原始 Agent 定义（用于 MD 删除后恢复）
            foreach (var agent in _agentStore.GetAllAsync().Result)
            {
                _originalAgents[agent.Name] = agent;
            }

            _logger.LogInformation("AgentManager 初始化完成，已注册 {Count} 个代理",
                _agentStore.GetAllAsync().Result.Count);
        }

        /// <inheritdoc/>
        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("AgentManager 正在初始化...");
            await _runtimeManager.InitializeAsync();
            await ApplyMdOverridesAsync();
            _logger.LogInformation("AgentManager 初始化完成");
        }

        /// <inheritdoc/>
        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// 加载所有 MD 配置并应用到 AgentStore
        /// </summary>
        private async Task ApplyMdOverridesAsync()
        {
            var mdInfos = await GetAllMdInfoAsync();
            foreach (var mdInfo in mdInfos)
            {
                try
                {
                    var config = ParseFile(mdInfo.FilePath);
                    if (config == null)
                    {
                        _logger.LogWarning("解析 MD 配置文件失败: {FilePath}，跳过", mdInfo.FilePath);
                        continue;
                    }

                    var cacheKey = $"{mdInfo.Level}:{mdInfo.Name}";
                    _configCache[cacheKey] = config;

                    await ApplyMdConfigToStoreAsync(mdInfo.Name, config);

                    _logger.LogInformation("已应用 MD 配置覆盖: {Name} ({Level})", mdInfo.Name, mdInfo.Level);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "应用 MD 配置覆盖失败: {Name}", mdInfo.Name);
                }
            }
        }

        #region Agent 发现和查询

        /// <inheritdoc/>
        public async Task<IReadOnlyList<AgentDefinition>> GetAgentsAsync()
        {
            return await _agentStore.GetAllAsync();
        }

        /// <inheritdoc/>
        public async Task<AgentDefinition?> GetAgentAsync(string name)
        {
            return await _agentStore.GetAsync(name);
        }

        /// <summary>
        /// 获取 Agent 并合并 MD 配置（IAgentRegistry 兼容）
        /// </summary>
        public async Task<AgentDefinition?> GetAgentWithMergedConfigAsync(
            string name,
            string? provider = null,
            string? model = null)
        {
            return await GetAgentAsync(name);
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<AgentDefinition>> GetSubAgentsAsync()
        {
            var allAgents = await _agentStore.GetAllAsync();
            var subAgents = allAgents
                .Where(a => a.Mode == AgentMode.SubAgent && !a.Disabled)
                .OrderBy(a => a.Name, StringComparer.Ordinal)
                .ToList();
            return subAgents.AsReadOnly();
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<AgentDefinition>> GetTaskableAgentsAsync()
        {
            var allAgents = await _agentStore.GetAllAsync();
            var taskable = allAgents
                .Where(a => !a.Disabled
                    && a.Mode != AgentMode.Primary
                    && a.Runtime == AgentRuntime.Native)
                .OrderBy(a => a.Name, StringComparer.Ordinal)
                .ToList();
            return taskable.AsReadOnly();
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<AgentDefinition>> GetPrimaryAgentsAsync()
        {
            var allAgents = await _agentStore.GetAllAsync();
            var primaryAgents = allAgents
                .Where(a => (a.Mode == AgentMode.Primary || a.Mode == AgentMode.All) && !a.IsHidden && !a.Disabled)
                .OrderBy(a => a.Name, StringComparer.Ordinal)
                .ToList();
            return primaryAgents.AsReadOnly();
        }

        /// <inheritdoc/>
        public bool HasAgent(string name)
        {
            return _agentStore.Has(name);
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<AgentDefinition>> GetAccessibleSubAgentsAsync(
            IReadOnlyList<PermissionRuleEntry> callerPermissions)
        {
            var subAgents = await GetSubAgentsAsync();

            var accessibleAgents = new List<AgentDefinition>();
            foreach (var agent in subAgents)
            {
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

        #endregion

        #region 默认 Agent 管理

        /// <inheritdoc/>
        public async Task<string> GetDefaultAgentNameAsync()
        {
            // 1. 优先检查运行时设置
            var runtimeDefault = await _runtimeManager.GetDefaultAgentNameAsync();
            if (runtimeDefault != null)
            {
                var agent = await GetAgentAsync(runtimeDefault);
                if (agent != null && (agent.Mode == AgentMode.Primary || agent.Mode == AgentMode.All) && !agent.IsHidden && !agent.Disabled)
                {
                    return runtimeDefault;
                }
            }

            // 2. 检查配置文件默认
            var configDefault = _options?.Value.DefaultAgent;
            if (configDefault != null)
            {
                var agent = await GetAgentAsync(configDefault);
                if (agent != null && (agent.Mode == AgentMode.Primary || agent.Mode == AgentMode.All) && !agent.IsHidden && !agent.Disabled)
                {
                    return configDefault;
                }
            }

            // 3. 优先查找 "build" Agent
            var buildAgent = await GetAgentAsync("build");
            if (buildAgent != null && !buildAgent.IsHidden && !buildAgent.Disabled)
            {
                return "build";
            }

            // 4. 回退：查找第一个可见的主代理
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
            await _runtimeManager.SetDefaultAgentAsync(name);
        }

        #endregion

        #region 运行时模型管理

        /// <inheritdoc/>
        public async Task UpdateAgentModelAsync(string agentName, ModelReference model)
        {
            await _runtimeManager.UpdateAgentModelAsync(agentName, model);
        }

        /// <inheritdoc/>
        public async Task<ModelReference?> GetEffectiveModelAsync(string agentName)
        {
            return await _runtimeManager.GetEffectiveModelAsync(agentName);
        }

        #endregion

        #region Agent 注册

        /// <inheritdoc/>
        public async Task RegisterAgentAsync(AgentDefinition agentInfo)
        {
            await _agentStore.RegisterAsync(agentInfo);
            _logger.LogInformation("注册代理: {Name}, Mode: {Mode}", agentInfo.Name, agentInfo.Mode);
        }

        /// <inheritdoc/>
        public bool UnregisterAgent(string name)
        {
            var removed = _agentStore.Unregister(name);
            if (removed)
            {
                _logger.LogInformation("注销代理: {Name}", name);
            }
            return removed;
        }

        /// <inheritdoc/>
        public IAgent? GetOrCreateAgentInstance(string name)
        {
            var info = _agentStore.GetAsync(name).Result;
            if (info == null)
                return null;

            if (info.AgentFactory != null)
            {
                return info.AgentFactory();
            }

            return new AgentInfoWrapper(info);
        }

        #endregion

        #region 配置编辑

        /// <inheritdoc/>
        public async Task<IReadOnlyList<AgentEditModel>> GetAllEditModelsAsync(CancellationToken ct = default)
        {
            var result = new List<AgentEditModel>();
            var agents = await GetAgentsAsync();
            var mdInfos = await GetAllMdInfoAsync(ct);

            foreach (var agent in agents)
            {
                var mdInfo = mdInfos.FirstOrDefault(m => m.Name == agent.Name);
                var model = ConvertToEditModel(agent, mdInfo);
                result.Add(model);
            }

            return result.AsReadOnly();
        }

        /// <inheritdoc/>
        public async Task<AgentEditModel?> GetEditModelAsync(string agentName, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(agentName);

            var agent = await GetAgentAsync(agentName);
            if (agent == null)
                return null;

            var mdInfos = await GetAllMdInfoAsync(ct);
            var mdInfo = mdInfos.FirstOrDefault(m => m.Name == agentName);

            return ConvertToEditModel(agent, mdInfo);
        }

        /// <inheritdoc/>
        public async Task<bool> SaveEditModelAsync(AgentEditModel model, ConfigLevel level, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(model);
            ArgumentException.ThrowIfNullOrEmpty(model.Name);

            var content = ConvertToMdContent(model);
            return await SaveMdContentAsync(model.Name, level, content, ct);
        }

        /// <inheritdoc/>
        public async Task<bool> SetAgentDisabledAsync(string name, bool disabled, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(name);

            var agent = await _agentStore.GetAsync(name);
            if (agent == null) return false;

            // 更新内存中的 Disabled
            agent.Disabled = disabled;

            // 同步到 MD 配置文件
            var mdInfos = await GetAllMdInfoAsync(ct);
            var mdInfo = mdInfos.FirstOrDefault(m => m.Name == name);

            if (mdInfo != null)
            {
                // 已有 MD 配置，更新字段
                var editModel = ConvertToEditModel(agent, mdInfo);
                editModel.Disabled = disabled;
                var content = ConvertToMdContent(editModel);
                return await SaveMdContentAsync(name, mdInfo.Level, content, ct);
            }
            else
            {
                // 无 MD 配置，创建一个覆盖
                var level = ConfigLevel.Project;
                var editModel = ConvertToEditModel(agent, null);
                editModel.Disabled = disabled;
                editModel.HasMdOverride = true;
                editModel.MdConfigLevel = level;
                var content = ConvertToMdContent(editModel);
                return await SaveMdContentAsync(name, level, content, ct);
            }
        }

        #endregion

        #region MD 配置文件管理

        /// <inheritdoc/>
        public async Task<IReadOnlyList<AgentMdInfo>> GetAllMdInfoAsync(CancellationToken ct = default)
        {
            var result = new List<AgentMdInfo>();

            // 用户级目录
            var userAgentsDir = Path.Combine(_workspaceProvider.UserSeeingDirectory, "agents");
            if (Directory.Exists(userAgentsDir))
            {
                foreach (var agentDir in Directory.GetDirectories(userAgentsDir))
                {
                    var agentMdFile = Path.Combine(agentDir, "AGENT.md");
                    if (File.Exists(agentMdFile))
                    {
                        var info = CreateAgentMdInfo(agentMdFile, ConfigLevel.User);
                        if (info != null) result.Add(info);
                    }
                }
            }

            // 项目级目录
            var projectAgentsDir = Path.Combine(_workspaceProvider.ProjectSeeingDirectory, "agents");
            if (Directory.Exists(projectAgentsDir))
            {
                foreach (var agentDir in Directory.GetDirectories(projectAgentsDir))
                {
                    var agentMdFile = Path.Combine(agentDir, "AGENT.md");
                    if (File.Exists(agentMdFile))
                    {
                        var info = CreateAgentMdInfo(agentMdFile, ConfigLevel.Project);
                        if (info != null) result.Add(info);
                    }
                }
            }

            return await Task.FromResult(result.AsReadOnly());
        }

        /// <inheritdoc/>
        public async Task<string?> GetMdContentAsync(string name, ConfigLevel level, CancellationToken ct = default)
        {
            var filePath = GetMdFilePath(name, level);
            if (!File.Exists(filePath))
                return null;

            return await File.ReadAllTextAsync(filePath, ct);
        }

        /// <inheritdoc/>
        public async Task<AgentEditModel> CreateMdConfigAsync(string name, ConfigLevel level, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(name);

            var filePath = GetMdFilePath(name, level);
            var directory = Path.GetDirectoryName(filePath)!;

            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (File.Exists(filePath))
            {
                throw new InvalidOperationException($"Agent '{name}' 的配置文件已存在");
            }

            var content = GetDefaultMdTemplate(name);
            await File.WriteAllTextAsync(filePath, content, ct);

            // 解析配置并应用到 Store
            var config = ParseContent(content);
            if (config != null)
            {
                var cacheKey = $"{level}:{name}";
                _configCache[cacheKey] = config;
                await ApplyMdConfigToStoreAsync(name, config);
            }

            var mdInfo = new AgentMdInfo
            {
                Name = name,
                Level = level,
                FilePath = filePath,
                LastModified = File.GetLastWriteTimeUtc(filePath)
            };

            // 从注册表获取 Agent 信息（如果是内置 Agent）
            var currentAgent = await _agentStore.GetAsync(name);
            var model = currentAgent != null
                ? ConvertToEditModel(currentAgent, mdInfo)
                : new AgentEditModel { Name = name, HasMdOverride = true, MdConfigLevel = level };

            OnConfigChanged(name, level, ConfigChangeAction.Created);

            _logger.LogInformation("创建 Agent '{Name}' MD 配置文件: {FilePath}", name, filePath);
            return model;
        }

        /// <inheritdoc/>
        public async Task<bool> SaveMdContentAsync(string name, ConfigLevel level, string content, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(name);
            ArgumentException.ThrowIfNullOrEmpty(content);

            var filePath = GetMdFilePath(name, level);

            // 确保目录存在
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var config = ParseContent(content);
            if (config == null)
            {
                _logger.LogWarning("保存失败：内容格式无效");
                return false;
            }

            await File.WriteAllTextAsync(filePath, content, ct);

            var cacheKey = $"{level}:{name}";
            _configCache[cacheKey] = config;

            // 合并 MD 配置到 AgentStore，确保实时生效
            await ApplyMdConfigToStoreAsync(name, config);

            OnConfigChanged(name, level, ConfigChangeAction.Updated);

            _logger.LogInformation("保存 Agent '{Name}' MD 配置文件: {FilePath}", name, filePath);
            return true;
        }

        /// <inheritdoc/>
        public async Task<bool> DeleteMdConfigAsync(string name, ConfigLevel level, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(name);

            var filePath = GetMdFilePath(name, level);
            var agentDir = Path.GetDirectoryName(filePath);

            if (!File.Exists(filePath))
            {
                _logger.LogWarning("删除失败：Agent '{Name}' 的配置文件不存在", name);
                return false;
            }

            await Task.Run(() =>
            {
                File.Delete(filePath);
                // 如果目录为空，删除目录
                if (!string.IsNullOrEmpty(agentDir) && Directory.Exists(agentDir))
                {
                    if (!Directory.EnumerateFileSystemEntries(agentDir).Any())
                    {
                        Directory.Delete(agentDir);
                    }
                }
            }, ct);

            var cacheKey = $"{level}:{name}";
            _configCache.TryRemove(cacheKey, out _);

            // 恢复原始 Agent 定义或移除
            await RemoveMdConfigFromStoreAsync(name);

            OnConfigChanged(name, level, ConfigChangeAction.Deleted);

            _logger.LogInformation("删除 Agent '{Name}' MD 配置文件: {FilePath}", name, filePath);
            return true;
        }

        /// <inheritdoc/>
        public string GetMdFilePath(string name, ConfigLevel level)
        {
            var baseDir = level == ConfigLevel.User
                ? _workspaceProvider.UserSeeingDirectory
                : _workspaceProvider.ProjectSeeingDirectory;
            return Path.Combine(baseDir, "agents", name, "AGENT.md");
        }

        /// <inheritdoc/>
        public string GetDefaultMdTemplate(string agentName)
        {
            return $@"---
name: {agentName}
description: Agent 描述
mode: Primary
category: general
provider: openai
model: gpt-4o
maxSteps: 50
---

# 系统提示词

你是一个 AI 助手，负责...
";
        }

        #endregion

        #region 私有方法

        /// <summary>
        /// 应用 MD 配置到 AgentStore（备份原始定义 → 合并 → 更新 Store）
        /// </summary>
        private async Task ApplyMdConfigToStoreAsync(string name, AgentConfigFile config)
        {
            // 从原始定义合并（确保增量覆盖正确）
            if (_originalAgents.TryGetValue(name, out var originalAgent))
            {
                // 已有原始定义，直接合并
                var merged = AgentDefinitionExtensions.Merge(originalAgent, config);
                await _agentStore.RegisterAsync(merged);
            }
            else
            {
                // 首次覆盖，备份当前 Store 中的定义
                var currentAgent = await _agentStore.GetAsync(name);
                if (currentAgent != null)
                {
                    _originalAgents[name] = currentAgent;
                    var merged = AgentDefinitionExtensions.Merge(currentAgent, config);
                    await _agentStore.RegisterAsync(merged);
                }
                else
                {
                    // 不存在内置 Agent，创建新定义
                    var newAgent = new AgentDefinition
                    {
                        Name = config.Name ?? name,
                        Description = config.Description,
                        Mode = Enum.TryParse<AgentMode>(config.Mode, ignoreCase: true, out var mode) ? mode : AgentMode.Primary,
                        Category = config.Category,
                        SystemPrompt = config.SystemPrompt,
                        Model = !string.IsNullOrEmpty(config.Model)
                            ? new ModelReference { ProviderId = config.Provider ?? "", ModelId = config.Model }
                            : null,
                        MaxSteps = config.MaxSteps,
                        Temperature = config.Temperature,
                        TopP = config.TopP,
                        MaxTokens = config.MaxTokens,
                        IsHidden = config.IsHidden ?? false,
                        Disabled = config.Disabled ?? false,
                        PermissionRules = config.PermissionRules ?? new(),
                        PermissionDefaultEffect = Enum.TryParse<PermissionEffect>(config.PermissionDefaultEffect, ignoreCase: true, out var effect)
                            ? effect
                            : PermissionEffect.Ask,
                        AllowedTools = config.AllowedTools ?? new(),
                        DeniedTools = config.DeniedTools ?? new(),
                        AcpBackend = config.AcpBackend
                    };
                    await _agentStore.RegisterAsync(newAgent);
                }
            }
        }

        /// <summary>
        /// 从 AgentStore 移除 MD 配置（恢复原始定义或删除）
        /// </summary>
        private async Task RemoveMdConfigFromStoreAsync(string name)
        {
            if (_originalAgents.TryRemove(name, out var originalAgent))
            {
                // 存在原始定义，恢复到 Store
                await _agentStore.RegisterAsync(originalAgent);
                _logger.LogInformation("已恢复 Agent '{Name}' 的原始定义", name);
            }
            else
            {
                // 不存在原始定义（纯 MD 创建的 Agent），从 Store 移除
                _agentStore.Unregister(name);
                _logger.LogInformation("已移除纯 MD 定义的 Agent '{Name}'", name);
            }
        }

        private AgentMdInfo? CreateAgentMdInfo(string filePath, ConfigLevel level)
        {
            var config = ParseFile(filePath);
            if (config == null) return null;

            return new AgentMdInfo
            {
                Name = config.Name,
                Description = config.Description,
                Level = level,
                FilePath = filePath,
                LastModified = File.GetLastWriteTimeUtc(filePath)
            };
        }

        private AgentConfigFile? ParseFile(string filePath)
        {
            try
            {
                var content = File.ReadAllText(filePath);
                var frontMatterEnd = content.IndexOf("---", 3);
                if (frontMatterEnd == -1)
                {
                    _logger.LogWarning("配置文件 '{FilePath}' 缺少 YAML Front Matter", filePath);
                    return null;
                }

                var yamlContent = content.Substring(3, frontMatterEnd - 3).Trim();
                var bodyContent = content.Substring(frontMatterEnd + 3).Trim();

                var config = _yamlDeserializer.Deserialize<AgentConfigFile>(yamlContent);

                if (string.IsNullOrEmpty(config?.Name))
                {
                    _logger.LogWarning("配置文件 '{FilePath}' 缺少 name 字段", filePath);
                    return null;
                }

                config.SystemPrompt = bodyContent;
                return config;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "解析配置文件 '{FilePath}' 失败", filePath);
                return null;
            }
        }

        private AgentConfigFile? ParseContent(string content)
        {
            try
            {
                var frontMatterEnd = content.IndexOf("---", 3);
                if (frontMatterEnd == -1) return null;

                var yamlContent = content.Substring(3, frontMatterEnd - 3).Trim();
                var bodyContent = content.Substring(frontMatterEnd + 3).Trim();
                var config = _yamlDeserializer.Deserialize<AgentConfigFile>(yamlContent);

                if (string.IsNullOrEmpty(config?.Name)) return null;

                config.SystemPrompt = bodyContent;
                return config;
            }
            catch
            {
                return null;
            }
        }

        private static AgentEditModel ConvertToEditModel(AgentDefinition agent, AgentMdInfo? mdInfo)
        {
            return new AgentEditModel
            {
                Name = agent.Name,
                Description = agent.Description,
                Mode = agent.Mode,
                Category = agent.Category,
                SystemPrompt = agent.SystemPrompt,
                Provider = agent.Model?.ProviderId,
                Model = agent.Model?.ModelId,
                Temperature = agent.Temperature,
                TopP = agent.TopP,
                MaxTokens = agent.MaxTokens,
                MaxSteps = agent.MaxSteps,
                IsHidden = agent.IsHidden,
                Disabled = agent.Disabled,
                PermissionDefaultEffect = agent.PermissionDefaultEffect,
                PermissionRules = agent.PermissionRules?.ToList() ?? new List<PermissionRuleEntry>(),
                AllowedTools = agent.AllowedTools?.ToList() ?? new List<string>(),
                DeniedTools = agent.DeniedTools?.ToList() ?? new List<string>(),
                IsBuiltIn = agent.IsNative,
                HasMdOverride = mdInfo != null,
                MdConfigLevel = mdInfo?.Level
            };
        }

        private string ConvertToMdContent(AgentEditModel model)
        {
            // 将权限规则转换为简化格式（只保留核心字段）
            var simplifiedRules = model.PermissionRules.Count > 0
                ? model.PermissionRules.Select(r => new
                {
                    kind = r.Kind.ToString(),
                    pattern = r.Pattern,
                    effect = r.Effect.ToString(),
                    priority = r.Priority
                }).ToList()
                : null;

            var frontMatter = new Dictionary<string, object?>
            {
                ["name"] = model.Name,
                ["description"] = model.Description,
                ["mode"] = model.Mode.ToString(),
                ["category"] = model.Category,
                ["provider"] = model.Provider,
                ["model"] = model.Model,
                ["temperature"] = model.Temperature,
                ["topP"] = model.TopP,
                ["maxTokens"] = model.MaxTokens,
                ["maxSteps"] = model.MaxSteps,
                ["isHidden"] = model.IsHidden ? true : null,
                ["disabled"] = model.Disabled ? true : null,
                ["permissionDefaultEffect"] = model.PermissionDefaultEffect != PermissionEffect.Ask
                    ? model.PermissionDefaultEffect.ToString()
                    : null,
                ["permissionRules"] = simplifiedRules,
                ["allowedTools"] = model.AllowedTools.Count > 0 ? model.AllowedTools : null,
                ["deniedTools"] = model.DeniedTools.Count > 0 ? model.DeniedTools : null
            };

            var yaml = _yamlSerializer.Serialize(frontMatter);
            var systemPrompt = model.SystemPrompt ?? "";

            return $"---\n{yaml}\n---\n\n{systemPrompt}";
        }

        /// <inheritdoc/>
        public string GenerateMdContent(AgentEditModel model)
        {
            return ConvertToMdContent(model);
        }

        private static bool WildcardMatches(string pattern, string input)
        {
            if (pattern == "*") return true;
            if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(input)) return false;

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

        protected virtual void OnConfigChanged(string name, ConfigLevel level, ConfigChangeAction action)
        {
            ConfigChanged?.Invoke(this, new AgentConfigChangedEventArgs
            {
                Name = name,
                Level = level,
                Action = action
            });
        }

        /// <summary>
        /// 代理信息包装器
        /// </summary>
        private class AgentInfoWrapper : IAgent
        {
            private readonly AgentDefinition _info;

            public AgentInfoWrapper(AgentDefinition info) => _info = info;

            public string Name { get => _info.Name; set => _info.Name = value; }
            public AgentMode Mode { get => _info.Mode; set => _info.Mode = value; }
            public string Description { get => _info.Description ?? string.Empty; set => _info.Description = value; }
            public IReadOnlyList<PermissionRuleEntry> PermissionRules { get => _info.PermissionRules.AsReadOnly(); set { _info.PermissionRules.Clear(); _info.PermissionRules.AddRange(value); } }
            public string? SystemPrompt { get => _info.SystemPrompt; set => _info.SystemPrompt = value; }
            public ModelReference? Model { get => _info.Model; set => _info.Model = value; }
            public int? MaxSteps { get => _info.MaxSteps; set => _info.MaxSteps = value; }
            public double? Temperature { get => _info.Temperature; set => _info.Temperature = value; }
            public double? TopP { get => _info.TopP; set => _info.TopP = value; }
            public int? MaxTokens { get => _info.MaxTokens; set => _info.MaxTokens = value; }
            public AgentStatus Status { get => _info.AgentFactory != null ? AgentStatus.Ready : AgentStatus.RequiresFactory; set { } }
            public bool Disabled { get => _info.Disabled; set => _info.Disabled = value; }
            public IReadOnlyList<string> AllowedTools { get => _info.AllowedTools.AsReadOnly(); set { _info.AllowedTools.Clear(); _info.AllowedTools.AddRange(value); } }
            public IReadOnlyList<string> DeniedTools { get => _info.DeniedTools.AsReadOnly(); set { _info.DeniedTools.Clear(); _info.DeniedTools.AddRange(value); } }
            public PermissionEffect PermissionDefaultEffect { get => _info.PermissionDefaultEffect; set => _info.PermissionDefaultEffect = value; }
            public AgentRuntime Runtime { get => _info.Runtime; set => _info.Runtime = value; }
            public string? AcpBackend { get => _info.AcpBackend; set => _info.AcpBackend = value; }

            public async IAsyncEnumerable<ChatMessage> ExecuteAsync(
                ChatMessage input,
                AgentContext context,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                yield return new ChatMessage
                {
                    Role = "system",
                    Content = $"代理 {_info.Name} 需要通过 AgentFactory 创建实例才能执行"
                };
            }
        }

        #endregion
    }
}
