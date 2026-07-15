using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Seeing.Agent.Configuration
{
    /// <summary>
    /// Agent 配置加载器接口
    /// </summary>
    public interface IAgentConfigLoader
    {
        /// <summary>
        /// 发现所有 Agent 配置文件
        /// </summary>
        Task<IReadOnlyList<string>> DiscoverAsync(CancellationToken ct = default);

        /// <summary>
        /// 加载并合并 Agent 配置
        /// </summary>
        Task<AgentDefinition?> LoadAsync(
            string agentName,
            string? provider = null,
            string? model = null,
            CancellationToken ct = default);

        /// <summary>
        /// 解析 MD 配置文件
        /// </summary>
        AgentConfigFile? ParseFile(string filePath);

        /// <summary>
        /// 获取所有层级的 MD 配置信息
        /// </summary>
        Task<IReadOnlyList<AgentMdInfo>> GetAllWithLevelAsync(CancellationToken ct = default);

        /// <summary>
        /// 创建新的 MD 配置文件
        /// </summary>
        Task<AgentConfigFile> CreateAsync(string name, ConfigLevel level, string? template = null, CancellationToken ct = default);

        /// <summary>
        /// 保存 MD 配置文件
        /// </summary>
        Task<bool> SaveAsync(string name, ConfigLevel level, string content, CancellationToken ct = default);

        /// <summary>
        /// 删除 MD 配置文件
        /// </summary>
        Task<bool> DeleteAsync(string name, ConfigLevel level, CancellationToken ct = default);

        /// <summary>
        /// 获取配置文件路径
        /// </summary>
        string GetFilePath(string name, ConfigLevel level);

        /// <summary>
        /// 配置变更事件
        /// </summary>
        event EventHandler<AgentConfigChangedEventArgs>? ConfigChanged;
    }

    /// <summary>
    /// Agent 配置加载器 - 发现、解析、合并 MD 配置文件
    /// </summary>
    public class AgentConfigLoader : IAgentConfigLoader
    {
        private readonly IWorkspaceProvider _workspaceProvider;
        private readonly ILogger<AgentConfigLoader> _logger;
        private readonly IDeserializer _yamlDeserializer;
        private readonly ConcurrentDictionary<string, AgentConfigFile> _cache = new();

        /// <inheritdoc/>
        public event EventHandler<AgentConfigChangedEventArgs>? ConfigChanged;

        public AgentConfigLoader(
            IWorkspaceProvider workspaceProvider,
            ILogger<AgentConfigLoader> logger)
        {
            _workspaceProvider = workspaceProvider;
            _logger = logger;
            _yamlDeserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<string>> DiscoverAsync(CancellationToken ct = default)
        {
            var files = new List<string>();

            // 用户级目录
            var userAgentsDir = Path.Combine(_workspaceProvider.UserSeeingDirectory, "agents");
            if (Directory.Exists(userAgentsDir))
            {
                files.AddRange(Directory.GetFiles(userAgentsDir, "*.md"));
            }

            // 项目级目录
            var projectAgentsDir = Path.Combine(_workspaceProvider.ProjectSeeingDirectory, "agents");
            if (Directory.Exists(projectAgentsDir))
            {
                files.AddRange(Directory.GetFiles(projectAgentsDir, "*.md"));
            }

            _logger.LogDebug("发现 {Count} 个 Agent 配置文件", files.Count);
            return await Task.FromResult(files.AsReadOnly());
        }

        /// <inheritdoc/>
        public async Task<AgentDefinition?> LoadAsync(
            string agentName,
            string? provider = null,
            string? model = null,
            CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(agentName);
            
            var files = await DiscoverAsync(ct);
            
            // 按优先级分组：项目级 > 用户级
            var userFile = files.FirstOrDefault(f =>
                f.StartsWith(_workspaceProvider.UserSeeingDirectory) &&
                GetAgentNameFromPath(f).Equals(agentName, StringComparison.OrdinalIgnoreCase));
            
            var projectFile = files.FirstOrDefault(f =>
                f.StartsWith(_workspaceProvider.ProjectSeeingDirectory) &&
                GetAgentNameFromPath(f).Equals(agentName, StringComparison.OrdinalIgnoreCase));

            AgentConfigFile? userConfig = null;
            AgentConfigFile? projectConfig = null;

            if (userFile != null)
            {
                userConfig = ParseFile(userFile);
            }

            if (projectFile != null)
            {
                projectConfig = ParseFile(projectFile);
            }

            // 如果没有任何配置文件
            if (userConfig == null && projectConfig == null)
            {
                _logger.LogDebug("未找到 Agent '{AgentName}' 的 MD 配置文件", agentName);
                return null;
            }

            // 构建基础定义
            var baseDef = new AgentDefinition { Name = agentName };

            // 合并配置：用户级 → 项目级
            var result = AgentDefinitionExtensions.Merge(baseDef, userConfig);
            result = AgentDefinitionExtensions.Merge(result, projectConfig);

            // 应用变体
            if (!string.IsNullOrEmpty(provider))
            {
                result = AgentDefinitionExtensions.ApplyVariant(result, provider, model);
            }

            _logger.LogDebug("加载 Agent '{AgentName}' 配置完成", agentName);
            return result;
        }

        /// <summary>
        /// 解析 MD 配置文件
        /// </summary>
        public AgentConfigFile? ParseFile(string filePath)
        {
            try
            {
                var content = File.ReadAllText(filePath);

                // 提取 YAML Front Matter
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

        /// <inheritdoc/>
        public async Task<IReadOnlyList<AgentMdInfo>> GetAllWithLevelAsync(CancellationToken ct = default)
        {
            var result = new List<AgentMdInfo>();

            // 用户级目录
            var userAgentsDir = Path.Combine(_workspaceProvider.UserSeeingDirectory, "agents");
            if (Directory.Exists(userAgentsDir))
            {
                foreach (var file in Directory.GetFiles(userAgentsDir, "*.md"))
                {
                    var info = CreateAgentMdInfo(file, ConfigLevel.User);
                    if (info != null) result.Add(info);
                }
            }

            // 项目级目录
            var projectAgentsDir = Path.Combine(_workspaceProvider.ProjectSeeingDirectory, "agents");
            if (Directory.Exists(projectAgentsDir))
            {
                foreach (var file in Directory.GetFiles(projectAgentsDir, "*.md"))
                {
                    var info = CreateAgentMdInfo(file, ConfigLevel.Project);
                    if (info != null) result.Add(info);
                }
            }

            return await Task.FromResult(result.AsReadOnly());
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
                VariantCount = config.Variants?.Count ?? 0,
                LastModified = File.GetLastWriteTimeUtc(filePath)
            };
        }

        /// <inheritdoc/>
        public async Task<AgentConfigFile> CreateAsync(string name, ConfigLevel level, string? template = null, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(name);

            var filePath = GetFilePath(name, level);
            var directory = Path.GetDirectoryName(filePath)!;

            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (File.Exists(filePath))
            {
                throw new InvalidOperationException($"Agent '{name}' 的配置文件已存在");
            }

            var content = template ?? GetDefaultTemplate(name);
            await File.WriteAllTextAsync(filePath, content, ct);

            var config = ParseFile(filePath);
            if (config == null)
            {
                throw new InvalidOperationException("创建的配置文件解析失败");
            }

            var cacheKey = $"{level}:{name}";
            _cache[cacheKey] = config;
            OnConfigChanged(name, level, ConfigChangeAction.Created);

            _logger.LogInformation("创建 Agent '{Name}' MD 配置文件: {FilePath}", name, filePath);
            return config;
        }

        /// <inheritdoc/>
        public async Task<bool> SaveAsync(string name, ConfigLevel level, string content, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(name);
            ArgumentException.ThrowIfNullOrEmpty(content);

            var filePath = GetFilePath(name, level);

            // 验证内容格式
            var config = ParseContent(content);
            if (config == null)
            {
                _logger.LogWarning("保存失败：内容格式无效");
                return false;
            }

            await File.WriteAllTextAsync(filePath, content, ct);

            var cacheKey = $"{level}:{name}";
            _cache[cacheKey] = config;
            OnConfigChanged(name, level, ConfigChangeAction.Updated);

            _logger.LogInformation("保存 Agent '{Name}' MD 配置文件: {FilePath}", name, filePath);
            return true;
        }

        /// <inheritdoc/>
        public async Task<bool> DeleteAsync(string name, ConfigLevel level, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(name);

            var filePath = GetFilePath(name, level);

            if (!File.Exists(filePath))
            {
                _logger.LogWarning("删除失败：Agent '{Name}' 的配置文件不存在", name);
                return false;
            }

            await Task.Run(() => File.Delete(filePath), ct);

            var cacheKey = $"{level}:{name}";
            _cache.TryRemove(cacheKey, out _);
            OnConfigChanged(name, level, ConfigChangeAction.Deleted);

            _logger.LogInformation("删除 Agent '{Name}' MD 配置文件: {FilePath}", name, filePath);
            return true;
        }

        /// <summary>
        /// 获取配置文件路径
        /// </summary>
        public string GetFilePath(string name, ConfigLevel level)
        {
            var baseDir = level == ConfigLevel.User
                ? _workspaceProvider.UserSeeingDirectory
                : _workspaceProvider.ProjectSeeingDirectory;
            return Path.Combine(baseDir, "agents", $"{name}.md");
        }

        /// <summary>
        /// 获取默认模板
        /// </summary>
        public static string GetDefaultTemplate(string agentName)
        {
            return $@"---
name: {agentName}
description: Agent 描述
mode: Primary
category: general
maxSteps: 50
variants: {{}}
---

# 系统提示词

你是一个 AI 助手，负责...
";
        }

        /// <summary>
        /// 解析内容（不读取文件）
        /// </summary>
        private AgentConfigFile? ParseContent(string content)
        {
            try
            {
                var frontMatterEnd = content.IndexOf("---", 3);
                if (frontMatterEnd == -1) return null;

                var yamlContent = content.Substring(3, frontMatterEnd - 3).Trim();
                var config = _yamlDeserializer.Deserialize<AgentConfigFile>(yamlContent);

                return string.IsNullOrEmpty(config?.Name) ? null : config;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 触发配置变更事件
        /// </summary>
        protected virtual void OnConfigChanged(string name, ConfigLevel level, ConfigChangeAction action)
        {
            ConfigChanged?.Invoke(this, new AgentConfigChangedEventArgs
            {
                Name = name,
                Level = level,
                Action = action
            });
        }

        private static string GetAgentNameFromPath(string path)
        {
            return Path.GetFileNameWithoutExtension(path);
        }
    }
}
