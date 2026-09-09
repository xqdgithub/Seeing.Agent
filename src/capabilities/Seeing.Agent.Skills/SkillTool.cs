using Seeing.Agent.Abstractions.Tools;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Skills;
using System.Text;
using System.Text.Json;

namespace Seeing.Agent.Skills
{
    /// <summary>
    /// 技能工具 - 让 LLM 加载技能内容注入上下文
    /// <para>
    /// 技能是上下文提供者，不是可执行单元。LLM 通过此工具加载技能内容，
    /// 然后遵循技能中的指令执行任务。
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
        /// 构建工具描述 — 纯加载器：技能目录由系统提示词 <c>## Skills</c> 节承载（含兜底注入），
        /// 本工具只负责按名加载技能全文，避免重复维护列表导致描述膨胀。
        /// </summary>
        private string BuildDescription()
        {
            return "Load a specialized skill that provides domain-specific instructions and workflows. " +
                   "The available skill names are listed in the system prompt's '## Skills' section. " +
                   "Call this tool with a skill 'name' to load its full instructions into the context.";
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
                        description = "The name of the skill to load (see '## Skills' section in the system prompt)."
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

            // 检查技能是否启用
            if (!_skillManager.IsSkillEnabled(skillName))
            {
                _logger.LogWarning("尝试加载已禁用的技能: {SkillName}", skillName);
                return Task.FromResult(Failure($"Skill '{skillName}' is disabled. Enable it in the Skills settings."));
            }

            // 获取技能信息
            var skill = _skillManager.GetSkillInfo(skillName);
            if (skill == null)
            {
                var available = string.Join(", ", _skillManager.GetAllSkillInfos().Keys);
                return Task.FromResult(Failure($"Skill \"{skillName}\" not found. Available skills: {(string.IsNullOrEmpty(available) ? "none" : available)}"));
            }

            _logger.LogInformation("Loading skill: {Name}", skillName);

            // 获取技能文件列表
            var skillFiles = _skillManager.GetSkillFiles(skillName, MaxSkillFiles);

            // 构建技能内容输出
            var output = BuildSkillContent(skill, skillFiles);

            return Task.FromResult(new ToolResult
            {
                Success = true,
                Title = $"Loaded skill: {skill.Name}",
                Output = output,
                Metadata = new Dictionary<string, object>
                {
                    ["name"] = skill.Name,
                    ["dir"] = skill.DirectoryPath,
                    ["fileCount"] = skillFiles.Count
                }
            });
        }

        /// <summary>
        /// 构建技能内容输出（注入到 LLM 上下文）
        /// </summary>
        private string BuildSkillContent(SkillInfo skill, List<string> skillFiles)
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
            lines.Add($"Base directory for this skill: file:///{skill.DirectoryPath.Replace("\\", "/")}");
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
