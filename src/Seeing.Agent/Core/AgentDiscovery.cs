using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Seeing.Agent.Core
{
    /// <summary>
    /// Agent 发现服务 - 从文件系统发现和加载 Agent 定义
    /// <para>
    /// 支持：
    /// - 从 .seeing/agent 或 .seeing/agents 目录扫描 .md 文件
    /// - 解析 Markdown frontmatter 获取 Agent 元数据
    /// - 支持全局和项目级别的 Agent 定义
    /// </para>
    /// </summary>
    public class AgentDiscovery
    {
        private readonly ILogger<AgentDiscovery> _logger;
        private readonly IDeserializer _yamlDeserializer;
        private readonly List<string> _searchDirectories = new();

        /// <summary>
        /// Agent 文件模式匹配
        /// </summary>
        private static readonly Regex FrontmatterRegex = new(
            @"^---\s*[\r]?\n(.*?)[\r]?\n---\s*[\r]?\n?",
            RegexOptions.Singleline | RegexOptions.Compiled);

        public AgentDiscovery(ILogger<AgentDiscovery> logger)
        {
            _logger = logger;
            _yamlDeserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();

            // 添加默认搜索目录
            AddDefaultDirectories();
        }

        /// <summary>
        /// 添加默认搜索目录
        /// </summary>
        private void AddDefaultDirectories()
        {
            // 项目级目录
            AddSearchDirectory("./.agents/agents");
            AddSearchDirectory("./.seeing/agents");

            // 全局目录
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(userProfile))
            {
                AddSearchDirectory(Path.Combine(userProfile, ".agents", "agents"));
                AddSearchDirectory(Path.Combine(userProfile, ".seeing", "agents"));
            }
        }

        /// <summary>
        /// 添加搜索目录
        /// </summary>
        public void AddSearchDirectory(string directory)
        {
            if (string.IsNullOrEmpty(directory)) return;

            var resolvedPath = ResolvePath(directory);
            if (!_searchDirectories.Contains(resolvedPath))
            {
                _searchDirectories.Add(resolvedPath);
                _logger.LogDebug("添加 Agent 搜索目录: {Directory}", resolvedPath);
            }
        }

        /// <summary>
        /// 解析路径
        /// </summary>
        private string ResolvePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;

            // 环境变量展开
            path = Environment.ExpandEnvironmentVariables(path);

            // 用户目录展开
            if (path.StartsWith("~"))
            {
                var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                path = path.Replace("~", userProfile);
            }

            // 相对路径转绝对路径
            if (!Path.IsPathRooted(path))
            {
                path = Path.GetFullPath(path);
            }

            return path;
        }

        /// <summary>
        /// 发现所有 Agent
        /// </summary>
        public async Task<List<AgentInfo>> DiscoverAgentsAsync(CancellationToken cancellationToken = default)
        {
            var agents = new List<AgentInfo>();

            foreach (var directory in _searchDirectories)
            {
                if (!Directory.Exists(directory))
                {
                    _logger.LogDebug("目录不存在，跳过: {Directory}", directory);
                    continue;
                }

                var agentFiles = Directory.GetFiles(directory, "*.md", SearchOption.AllDirectories);
                _logger.LogDebug("在目录 {Directory} 中发现 {Count} 个 Agent 文件", directory, agentFiles.Length);

                foreach (var agentFile in agentFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        var agent = await ParseAgentFileAsync(agentFile, cancellationToken);
                        if (agent != null)
                        {
                            agents.Add(agent);
                            _logger.LogInformation("发现 Agent: {Name} ({File})", agent.Name, agentFile);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "解析 Agent 文件失败: {File}", agentFile);
                    }
                }
            }

            return agents;
        }

        /// <summary>
        /// 解析 Agent 文件
        /// </summary>
        private async Task<AgentInfo?> ParseAgentFileAsync(string filePath, CancellationToken cancellationToken)
        {
            var content = await File.ReadAllTextAsync(filePath, cancellationToken);

            // 解析 frontmatter
            var match = FrontmatterRegex.Match(content);
            if (!match.Success)
            {
                _logger.LogWarning("Agent 文件缺少 YAML frontmatter: {File}", filePath);
                return null;
            }

            var frontmatter = match.Groups[1].Value;
            var promptContent = content.Substring(match.Length).Trim();

            // 解析 YAML
            AgentFrontmatter? data;
            try
            {
                data = _yamlDeserializer.Deserialize<AgentFrontmatter>(frontmatter);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Agent frontmatter 解析失败: {File}", filePath);
                return null;
            }

            if (data == null || string.IsNullOrEmpty(data.Name))
            {
                // 使用文件名作为名称
                data ??= new AgentFrontmatter();
                data.Name = Path.GetFileNameWithoutExtension(filePath);
            }

            // 验证名称格式
            if (!IsValidAgentName(data.Name))
            {
                _logger.LogWarning("Agent 名称格式无效: {Name} (必须是字母数字+连字符)", data.Name);
                return null;
            }

            // 构建 AgentInfo
            var agentInfo = new AgentInfo
            {
                Name = data.Name,
                Description = data.Description,
                Mode = ParseAgentMode(data.Mode),
                IsNative = false,
                IsHidden = data.Hidden,
                Temperature = data.Temperature,
                TopP = data.TopP,
                Color = data.Color,
                SystemPrompt = string.IsNullOrEmpty(promptContent) ? data.Prompt : promptContent,
                MaxSteps = data.Steps ?? data.MaxSteps,
                Variant = data.Variant,
                Tags = data.Tags?.Split(',', ';').Select(t => t.Trim()).Where(t => !string.IsNullOrEmpty(t)).ToList() ?? new List<string>(),
                Category = data.Category,
                Permissions = ParsePermissions(data.Permission, data.Tools),
                Model = ParseModelReference(data.Model)
            };

            return agentInfo;
        }

        /// <summary>
        /// 验证 Agent 名称格式
        /// </summary>
        private static bool IsValidAgentName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return Regex.IsMatch(name, @"^[a-zA-Z][a-zA-Z0-9_-]*$");
        }

        /// <summary>
        /// 解析 Agent 模式
        /// </summary>
        private static AgentMode ParseAgentMode(string? mode)
        {
            return mode?.ToLowerInvariant() switch
            {
                "primary" => AgentMode.Primary,
                "subagent" => AgentMode.SubAgent,
                "all" => AgentMode.All,
                _ => AgentMode.All
            };
        }

        /// <summary>
        /// 解析权限配置
        /// </summary>
        private List<PermissionRule> ParsePermissions(Dictionary<string, object>? permission, Dictionary<string, bool>? tools)
        {
            var rules = new List<PermissionRule>();

            // 从 tools 配置生成权限规则（旧格式）
            if (tools != null)
            {
                foreach (var (tool, enabled) in tools)
                {
                    rules.Add(new PermissionRule
                    {
                        Permission = tool,
                        Pattern = "*",
                        Action = enabled ? PermissionAction.Allow : PermissionAction.Deny
                    });
                }
            }

            // 从 permission 配置生成权限规则（新格式）
            if (permission != null)
            {
                foreach (var (key, value) in permission)
                {
                    if (value is string actionStr)
                    {
                        var action = Enum.Parse<PermissionAction>(actionStr, true);
                        rules.Add(new PermissionRule
                        {
                            Permission = key,
                            Pattern = "*",
                            Action = action
                        });
                    }
                    else if (value is Dictionary<object, object> patterns)
                    {
                        foreach (var (patternKey, patternValue) in patterns)
                        {
                            if (patternValue is string patternActionStr)
                            {
                                var action = Enum.Parse<PermissionAction>(patternActionStr, true);
                                rules.Add(new PermissionRule
                                {
                                    Permission = key,
                                    Pattern = patternKey?.ToString() ?? "*",
                                    Action = action
                                });
                            }
                        }
                    }
                }
            }

            return rules;
        }

        /// <summary>
        /// 解析模型引用
        /// </summary>
        private static ModelReference? ParseModelReference(string? model)
        {
            if (string.IsNullOrEmpty(model)) return null;

            var parts = model.Split(new[] { ':', '/' }, 2);
            if (parts.Length >= 2)
            {
                return new ModelReference
                {
                    ProviderId = parts[0],
                    ModelId = parts[1]
                };
            }
            return null;
        }

        /// <summary>
        /// 获取所有搜索目录
        /// </summary>
        public IReadOnlyList<string> GetSearchDirectories() => _searchDirectories;

        /// <summary>
        /// 清空搜索目录
        /// </summary>
        public void ClearSearchDirectories()
        {
            _searchDirectories.Clear();
        }

        /// <summary>
        /// 重置为默认搜索目录
        /// </summary>
        public void ResetToDefaultDirectories()
        {
            _searchDirectories.Clear();
            AddDefaultDirectories();
        }
    }

    /// <summary>
    /// Agent Frontmatter 数据结构
    /// </summary>
    internal class AgentFrontmatter
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Mode { get; set; }
        public bool Hidden { get; set; }
        public double? Temperature { get; set; }
        public double? TopP { get; set; }
        public string? Color { get; set; }
        public string? Model { get; set; }
        public string? Variant { get; set; }
        public string? Prompt { get; set; }
        public int? Steps { get; set; }
        public int? MaxSteps { get; set; }
        public string? Category { get; set; }
        public string? Tags { get; set; }
        public Dictionary<string, object>? Permission { get; set; }
        public Dictionary<string, bool>? Tools { get; set; }
    }
}