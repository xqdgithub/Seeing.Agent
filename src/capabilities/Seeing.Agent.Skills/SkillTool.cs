using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Skills;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Skills.Catalog;
using System.Text.Json;

namespace Seeing.Agent.Skills
{
    /// <summary>
    /// 技能工具 - 让 LLM 加载技能内容注入上下文
    /// <para>
    /// 技能是上下文提供者，不是可执行单元。LLM 通过此工具加载技能内容，
    /// 然后遵循技能中的指令执行任务。
    /// </para>
    /// <para>
    /// 解析优先读执行级钉住快照（<see cref="ToolContext.SkillSnapshot"/>）：同一次执行内
    /// 「装配看到的清单」与「工具能加载的技能」是同一份事实，安装/删除中途也不会漂移；
    /// 仅在旧宿主未透传快照时才回退只读门面（目录当前态）。
    /// </para>
    /// </summary>
    public class SkillTool : ITool
    {
        private readonly SkillManager _skillManager;
        private readonly ILogger<SkillTool> _logger;

        /// <summary>
        /// 技能文件列表最大数量
        /// </summary>
        private const int MaxSkillFiles = 50;

        public SkillTool(SkillManager skillManager, ILogger<SkillTool> logger)
        {
            _skillManager = skillManager;
            _logger = logger;
        }

        public string Id => "skill";

        public string Description => BuildDescription();

        public ToolCategory Category => ToolCategory.LlmInteraction;

        /// <summary>工具标签</summary>
        public IReadOnlyList<string> Tags => Array.Empty<string>();

        public JsonElement ParametersSchema => BuildParametersSchema();

        /// <summary>
        /// 构建工具描述 — 纯加载器：技能清单由系统提示词 <c>## Skills</c> 区块（上下文基线）承载，
        /// 本工具只负责按名加载技能全文，避免重复维护列表导致描述膨胀。
        /// </summary>
        private string BuildDescription()
        {
            return "按名称加载技能全文；技能名见系统提示词的 Skills 节。\n\n" +
                   "- 技能名必须来自系统提示词「## Skills」节列出的名称。\n" +
                   "- 加载后按技能内的工作流与约定执行。\n" +
                   "- 不用于一般知识问答或代码搜索（用 `codesearch`）。";
        }

        /// <summary>
        /// 构建参数 Schema — name 必填，技能名见系统提示词 <c>## Skills</c> 节。
        /// </summary>
        private JsonElement BuildParametersSchema()
        {
            var schema = new
            {
                type = "object",
                properties = new
                {
                    name = new
                    {
                        type = "string",
                        description = "要加载的技能名称（取自系统提示词的「## Skills」节）"
                    }
                },
                required = new[] { "name" }
            };

            return JsonSerializer.SerializeToElement(schema);
        }

        public Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
        {
            // 获取技能名称
            if (!arguments.TryGetProperty("name", out var nameProp))
            {
                return Task.FromResult(Failure("Missing required parameter: name. See '## Skills' section in the system prompt for available skill names."));
            }

            var skillName = nameProp.GetString();
            if (string.IsNullOrEmpty(skillName))
            {
                return Task.FromResult(Failure("Parameter 'name' must be a non-empty string. See '## Skills' section in the system prompt for available skill names."));
            }

            SkillInfo skill;
            string directory;
            List<string> skillFiles;

            if (context.SkillSnapshot is { } snapshot)
            {
                // 执行级钉住：解析与文件列表都以本轮快照为准
                var entry = snapshot.Entries.FirstOrDefault(
                    candidate => string.Equals(candidate.Info.Name, skillName, StringComparison.OrdinalIgnoreCase));

                if (entry is null)
                {
                    if (snapshot.DisabledNames.Contains(skillName))
                        return Task.FromResult(Disabled(skillName));

                    return Task.FromResult(NotFound(skillName, snapshot));
                }

                if (snapshot.DisabledNames.Contains(entry.Info.Name))
                    return Task.FromResult(Disabled(skillName));

                skill = entry.Info;
                directory = entry.DirectoryPath;
                skillFiles = _skillManager.GetSkillFiles(entry, MaxSkillFiles);
            }
            else
            {
                // 兼容旧宿主（未透传钉住快照）：回退只读门面的目录当前态
                if (!_skillManager.IsSkillEnabled(skillName))
                    return Task.FromResult(Disabled(skillName));

                var info = _skillManager.GetSkillInfo(skillName);
                if (info is null)
                    return Task.FromResult(NotFound(skillName, null));

                skill = info;
                directory = Path.GetDirectoryName(info.Location) ?? string.Empty;
                skillFiles = _skillManager.GetSkillFiles(skillName, MaxSkillFiles);
            }

            _logger.LogInformation("Loading skill: {Name}", skillName);

            var output = BuildSkillContent(skill, directory, skillFiles);

            return Task.FromResult(new ToolResult
            {
                Success = true,
                Title = $"Loaded skill: {skill.Name}",
                Output = output,
                Metadata = new Dictionary<string, object>
                {
                    ["name"] = skill.Name,
                    ["dir"] = directory,
                    ["fileCount"] = skillFiles.Count
                }
            });
        }

        /// <summary>
        /// 构建技能内容输出（注入到 LLM 上下文）
        /// </summary>
        private string BuildSkillContent(SkillInfo skill, string directory, List<string> skillFiles)
        {
            var lines = new List<string>
            {
                $"<skill_content name=\"{EscapeXml(skill.Name)}\">",
                $"# Skill: {skill.Name}",
                ""
            };

            // 添加元数据
            if (!string.IsNullOrEmpty(skill.Version))
                lines.Add($"**Version:** {skill.Version}");
            if (!string.IsNullOrEmpty(skill.Author))
                lines.Add($"**Author:** {skill.Author}");
            if (!string.IsNullOrEmpty(skill.License))
                lines.Add($"**License:** {skill.License}");
            if (!string.IsNullOrEmpty(skill.Compatibility))
                lines.Add($"**Compatibility:** {skill.Compatibility}");
            if (skill.Tags.Count > 0)
                lines.Add($"**Tags:** {string.Join(", ", skill.Tags)}");
            if (skill.Requires.Count > 0)
                lines.Add($"**Requires:** {string.Join(", ", skill.Requires)}");
            if (skill.Metadata.Count > 0)
            {
                lines.Add($"**Metadata:**");
                foreach (var kvp in skill.Metadata)
                {
                    lines.Add($"  - {kvp.Key}: {kvp.Value}");
                }
            }

            if (!string.IsNullOrEmpty(skill.Version) || !string.IsNullOrEmpty(skill.Author) ||
                !string.IsNullOrEmpty(skill.License) || !string.IsNullOrEmpty(skill.Compatibility) ||
                skill.Tags.Count > 0 || skill.Requires.Count > 0 || skill.Metadata.Count > 0)
            {
                lines.Add("");
            }

            // 添加技能内容
            lines.Add(skill.Content);
            lines.Add("");
            lines.Add($"Base directory for this skill: file:///{directory.Replace("\\", "/")}");
            lines.Add("Relative paths in this skill (e.g., scripts/, references/) are relative to this base directory.");

            // 添加技能文件列表
            if (skillFiles.Count > 0)
            {
                lines.Add("");
                lines.Add("Note: file list is sampled.");
                lines.Add("<skill_files>");
                foreach (var file in skillFiles)
                {
                    var fileName = Path.GetFileName(file);
                    lines.Add($"  <file>{EscapeXml(fileName)}</file>");
                }
                lines.Add("</skill_files>");
            }

            lines.Add("</skill_content>");

            return string.Join("\n", lines);
        }

        /// <summary>「技能不存在」提示：清单文本复用 <see cref="SkillCatalogFormatter"/>（唯一出口）。</summary>
        private ToolResult NotFound(string skillName, SkillCatalogSnapshot? snapshot)
        {
            IReadOnlyList<SkillCatalogEntry> available = snapshot is null
                ? _skillManager.GetAllSkillInfos().Values
                    .Select(info => new SkillCatalogEntry(info, SkillSource.Registered, null, string.Empty))
                    .ToList()
                : snapshot.Entries
                    .Where(entry => !snapshot.DisabledNames.Contains(entry.Info.Name))
                    .ToList();

            return Failure(
                $"Skill \"{skillName}\" not found.\n\n{SkillCatalogFormatter.RenderList(available)}");
        }

        /// <summary>「技能已禁用」提示。</summary>
        private ToolResult Disabled(string skillName)
        {
            _logger.LogWarning("尝试加载已禁用的技能: {SkillName}", skillName);
            return Failure($"Skill '{skillName}' is disabled. Enable it in the Skills settings.");
        }

        /// <summary>
        /// XML 转义
        /// </summary>
        private static string EscapeXml(string text)
        {
            return text
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
        }

        /// <summary>
        /// 创建失败结果
        /// </summary>
        private ToolResult Failure(string message)
        {
            _logger.LogWarning("Skill tool failed: {Message}", message);
            return new ToolResult
            {
                Success = false,
                Title = "Skill loading failed",
                Output = message
            };
        }
    }
}
