using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Interfaces;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Seeing.Agent.Skills
{
    /// <summary>
    /// Skill 管理器 - 管理技能的发现和加载
    /// <para>
    /// 技能是上下文提供者，不是可执行单元。技能内容通过 SkillTool 注入到 LLM 上下文中。
    /// </para>
    /// </summary>
    public class SkillManager
    {
        private readonly ILogger<SkillManager> _logger;
        private readonly ConcurrentDictionary<string, SkillInfo> _skillInfos = new();
        private readonly List<string> _skillDirectories = new();

        /// <summary>
        /// 技能名称验证正则：小写字母数字，单个连字符分隔，1-64字符
        /// </summary>
        private static readonly Regex NamePattern = new(@"^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);

        /// <summary>
        /// 描述最大长度
        /// </summary>
        private const int MaxDescriptionLength = 1024;

        /// <summary>
        /// 技能名称最大长度
        /// </summary>
        private const int MaxNameLength = 64;

        public SkillManager(ILogger<SkillManager> logger)
        {
            _logger = logger;

            // 项目相对路径
            AddDefaultDirectory("./.agents/skills");
            AddDefaultDirectory("./.seeing/skills");

            // 用户目录（跨平台支持）
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(userProfile))
            {
                AddDefaultDirectory(Path.Combine(userProfile, ".agents", "skills"));
                AddDefaultDirectory(Path.Combine(userProfile, ".seeing", "skills"));
            }
        }

        /// <summary>
        /// 添加默认目录（自动解析路径）
        /// </summary>
        private void AddDefaultDirectory(string path)
        {
            var resolvedPath = ResolvePath(path);
            if (!_skillDirectories.Contains(resolvedPath))
            {
                _skillDirectories.Add(resolvedPath);
                _logger.LogDebug("添加默认技能目录: {Directory}", resolvedPath);
            }
        }

        /// <summary>
        /// 解析路径（支持相对路径、用户目录 ~、环境变量）
        /// </summary>
        private string ResolvePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;

            // 1. 环境变量展开（如 %USERPROFILE%）
            path = Environment.ExpandEnvironmentVariables(path);

            // 2. 用户目录展开（如 ~/.agents/skills）
            if (path.StartsWith("~"))
            {
                var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                path = path.Replace("~", userProfile);
            }

            // 3. 相对路径转绝对路径
            if (!Path.IsPathRooted(path))
            {
                path = Path.GetFullPath(path);
            }

            return path;
        }

        /// <summary>
        /// 将搜索目录重置为默认路径，用于切换工作区后重新挂载。
        /// </summary>
        public void ResetSearchDirectoriesToDefault()
        {
            _skillDirectories.Clear();

            // 项目相对路径
            AddDefaultDirectory("./.seeing/skills");
            AddDefaultDirectory("./.agents/skills");

            // 用户目录
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(userProfile))
            {
                AddDefaultDirectory(Path.Combine(userProfile, ".agents", "skills"));
                AddDefaultDirectory(Path.Combine(userProfile, ".seeing", "skills"));
            }
        }

        /// <summary>
        /// 清空所有技能搜索目录，供宿主按绝对路径重新挂载。
        /// </summary>
        public void ClearSearchDirectories()
        {
            _skillDirectories.Clear();
        }

        /// <summary>
        /// 清空所有已发现的技能信息，便于切换工作区后重新发现。
        /// </summary>
        public void ClearSkillInfos()
        {
            _skillInfos.Clear();
        }

        /// <summary>
        /// 添加技能搜索目录（支持相对路径、~ 用户目录、环境变量）
        /// </summary>
        public void AddSearchDirectory(string directory)
        {
            if (string.IsNullOrEmpty(directory)) return;

            var resolvedDirectory = ResolvePath(directory);
            if (!_skillDirectories.Contains(resolvedDirectory))
            {
                _skillDirectories.Add(resolvedDirectory);
                _logger.LogDebug("添加技能搜索目录: {Directory} (原始: {Original})", resolvedDirectory, directory);
            }
        }

        /// <summary>
        /// 发现所有技能（扫描 SKILL.md 文件）
        /// </summary>
        public async Task DiscoverSkillsAsync(CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("开始发现技能，搜索目录数量: {Count}", _skillDirectories.Count);

            foreach (var directory in _skillDirectories)
            {
                if (!Directory.Exists(directory)) continue;

                var skillFiles = Directory.GetFiles(directory, "SKILL.md", SearchOption.AllDirectories);
                foreach (var skillFile in skillFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var content = await File.ReadAllTextAsync(skillFile, cancellationToken);
                        var info = ParseSkillFile(skillFile, content);
                        if (info != null && !string.IsNullOrEmpty(info.Name))
                        {
                            // 重复技能检测
                            if (_skillInfos.ContainsKey(info.Name))
                            {
                                _logger.LogWarning("重复技能名称: {Name}, 已存在: {Existing}, 新: {Duplicate}",
                                    info.Name, _skillInfos[info.Name].Location, info.Location);
                            }

                            _skillInfos[info.Name] = info;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "解析技能文件失败: {File}", skillFile);
                    }
                }
            }

            _logger.LogInformation("发现完成，已加载 {Count} 个技能", _skillInfos.Count);
        }

        /// <summary>
        /// 获取技能信息
        /// </summary>
        public SkillInfo? GetSkillInfo(string name)
        {
            return string.IsNullOrEmpty(name) ? null : _skillInfos.TryGetValue(name, out var info) ? info : null;
        }

        /// <summary>
        /// 获取所有技能信息
        /// </summary>
        public IReadOnlyDictionary<string, SkillInfo> GetAllSkillInfos() => _skillInfos;

        /// <summary>
        /// 获取所有技能目录
        /// </summary>
        public IReadOnlyList<string> GetSkillDirectories() => _skillDirectories;

        /// <summary>
        /// 获取技能目录下的相关文件（排除 SKILL.md）
        /// </summary>
        public List<string> GetSkillFiles(string skillName, int limit = 10)
        {
            var skill = GetSkillInfo(skillName);
            if (skill == null || !Directory.Exists(skill.DirectoryPath))
                return new List<string>();

            try
            {
                return Directory.GetFiles(skill.DirectoryPath, "*", SearchOption.TopDirectoryOnly)
                    .Where(f => !Path.GetFileName(f).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
                    .Take(limit)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "获取技能文件列表失败: {Skill}", skillName);
                return new List<string>();
            }
        }

        /// <summary>
        /// 解析 SKILL.md 文件
        /// </summary>
        private SkillInfo? ParseSkillFile(string filePath, string content)
        {
            // 正则：支持 \n 和 \r\n 换行符，支持可选的尾随换行
            // Multiline: 让 ^ 匹配每行开头
            // Singleline: 让 . 匹配换行符
            var frontmatterMatch = Regex.Match(
                content,
                @"^---[\r]?[\n](.*?)[\r]?[\n]---[\r]?[\n]?",
                RegexOptions.Multiline | RegexOptions.Singleline);

            if (!frontmatterMatch.Success)
            {
                _logger.LogWarning("技能文件缺少 YAML frontmatter: {File}", filePath);
                return null;
            }

            var info = new SkillInfo { Location = filePath, Content = content.Substring(frontmatterMatch.Length).Trim() };
            var frontmatter = frontmatterMatch.Groups[1].Value;
            var currentKey = string.Empty;
            var metadataBuilder = new Dictionary<string, string>();

            foreach (var line in frontmatter.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#')) continue;

                // 处理缩进的 metadata 子字段
                if (line.StartsWith("  ") || line.StartsWith("\t"))
                {
                    if (!string.IsNullOrEmpty(currentKey) && currentKey == "metadata")
                    {
                        var metaColonIndex = trimmed.IndexOf(':');
                        if (metaColonIndex > 0)
                        {
                            var metaKey = trimmed.Substring(0, metaColonIndex).Trim();
                            var metaValue = trimmed.Substring(metaColonIndex + 1).Trim();
                            if (!string.IsNullOrEmpty(metaKey) && !string.IsNullOrEmpty(metaValue))
                            {
                                metadataBuilder[metaKey] = metaValue;
                            }
                        }
                    }
                    continue;
                }

                var colonIndex = trimmed.IndexOf(':');
                if (colonIndex < 0) continue;

                var key = trimmed.Substring(0, colonIndex).Trim().ToLowerInvariant();
                var value = trimmed.Substring(colonIndex + 1).Trim();
                currentKey = key;

                // 解析所有 Frontmatter 字段
                switch (key)
                {
                    case "name":
                        info.Name = value;
                        break;
                    case "description":
                        info.Description = value;
                        break;
                    case "version":
                        info.Version = value;
                        break;
                    case "author":
                        info.Author = value;
                        break;
                    case "license":
                        info.License = value;
                        break;
                    case "compatibility":
                        info.Compatibility = value;
                        break;
                    case "tags":
                        info.Tags = value.Split(',')
                            .Select(t => t.Trim())
                            .Where(t => !string.IsNullOrEmpty(t))
                            .ToList();
                        break;
                    case "requires":
                        info.Requires = value.Split(',')
                            .Select(r => r.Trim())
                            .Where(r => !string.IsNullOrEmpty(r))
                            .ToList();
                        break;
                    case "metadata":
                        // metadata 是一个字典，后续行处理
                        break;
                }
            }

            // 应用 metadata
            info.Metadata = metadataBuilder;

            // === 验证 ===

            // 验证名称
            if (string.IsNullOrEmpty(info.Name))
            {
                _logger.LogWarning("技能缺少 name 字段: {File}", filePath);
                return null;
            }

            // 名称长度验证
            if (info.Name.Length > MaxNameLength)
            {
                _logger.LogWarning("技能名称过长 ({Length} > {Max}): {Name}", info.Name.Length, MaxNameLength, info.Name);
                return null;
            }

            // 名称格式验证
            if (!NamePattern.IsMatch(info.Name))
            {
                _logger.LogWarning("技能名称格式无效 (必须是小写字母数字+连字符): {Name}", info.Name);
                return null;
            }

            // 名称与目录名匹配验证
            var dirName = Path.GetFileName(Path.GetDirectoryName(filePath));
            if (!string.IsNullOrEmpty(dirName) && info.Name != dirName)
            {
                _logger.LogWarning("技能名称 '{Name}' 与目录名 '{DirName}' 不匹配: {File}", info.Name, dirName, filePath);
                return null;
            }

            // 验证描述
            if (string.IsNullOrEmpty(info.Description))
            {
                _logger.LogWarning("技能缺少 description 字段: {File}", filePath);
                return null;
            }

            // 描述长度验证和截断
            if (info.Description.Length > MaxDescriptionLength)
            {
                _logger.LogWarning("技能描述过长 ({Length} > {Max})，已截断: {Name}", info.Description.Length, MaxDescriptionLength, info.Name);
                info.Description = info.Description.Substring(0, MaxDescriptionLength);
            }

            return info;
        }
    }
}