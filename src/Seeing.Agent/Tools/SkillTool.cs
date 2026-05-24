using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Skills;
using System.Text.Json;

namespace Seeing.Agent.Tools
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

        public JsonElement ParametersSchema => BuildParametersSchema();

        /// <summary>
        /// 构建工具描述，包含所有可用技能列表（已按权限过滤）
        /// </summary>
        private string BuildDescription()
        {
            var skills = _skillManager.GetAllSkillInfos().Values;

            if (!skills.Any())
            {
                return "Load a specialized skill that provides domain-specific instructions and workflows. No skills are currently available.";
            }

            var skillList = skills.Select(s =>
                $"    <skill>\n" +
                $"      <name>{EscapeXml(s.Name)}</name>\n" +
                $"      <description>{EscapeXml(s.Description)}</description>\n" +
                $"      <location>{EscapeXml(s.Location)}</location>\n" +
                $"    </skill>");

            return string.Join("\n", new[]
            {
                "Load a specialized skill that provides domain-specific instructions and workflows.",
                "",
                "When you recognize that a task matches one of the available skills listed below, use this tool to load the full skill instructions.",
                "",
                "The skill will inject detailed instructions, workflows, and access to bundled resources (scripts, references, templates) into the conversation context.",
                "",
                "Tool output includes a `<skill_content name=\"...\">` block with the loaded content.",
                "",
                "The following skills provide specialized sets of instructions for particular tasks.",
                "Invoke this tool to load a skill when a task matches one of the available skills listed below:",
                "",
                "<available_skills>",
                string.Join("\n", skillList),
                "</available_skills>"
            });
        }

        /// <summary>
        /// 构建参数 Schema
        /// </summary>
        private JsonElement BuildParametersSchema()
        {
            var skills = _skillManager.GetAllSkillInfos().Values;
            var examples = skills.Take(3).Select(s => s.Name).ToList();
            var hint = examples.Count > 0 ? $" (e.g., {string.Join(", ", examples)})" : "";

            var schema = new
            {
                type = "object",
                properties = new
                {
                    name = new
                    {
                        type = "string",
                        description = $"The name of the skill from available_skills{hint}"
                    }
                },
                required = new[] { "name" }
            };

            return JsonSerializer.SerializeToElement(schema);
        }

        public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
        {
            // 获取技能名称
            if (!arguments.TryGetProperty("name", out var nameProp))
            {
                return Failure("Missing required parameter: name");
            }

            var skillName = nameProp.GetString();
            if (string.IsNullOrEmpty(skillName))
            {
                return Failure("Parameter 'name' must be a non-empty string");
            }

            // 获取技能信息
            var skill = _skillManager.GetSkillInfo(skillName);
            if (skill == null)
            {
                var available = string.Join(", ", _skillManager.GetAllSkillInfos().Keys);
                return Failure($"Skill \"{skillName}\" not found. Available skills: {(string.IsNullOrEmpty(available) ? "none" : available)}");
            }

            // 权限检查由 AgentExecutor.EvaluatePermissionAsync 统一处理
            // 此处仅保留 AskPermission 回调（用于需要用户确认的场景）
            if (context.AskPermission != null)
            {
                await context.AskPermission(new PermissionRequest
                {
                    Permission = "skill",
                    Patterns = new List<string> { skillName },
                    Metadata = new Dictionary<string, object>
                    {
                        ["name"] = skill.Name,
                        ["description"] = skill.Description
                    }
                });
            }

            _logger.LogInformation("Loading skill: {Name}", skillName);

            // 获取技能文件列表
            var skillFiles = _skillManager.GetSkillFiles(skillName, MaxSkillFiles);

            // 构建技能内容输出
            var output = BuildSkillContent(skill, skillFiles);

            return new ToolResult
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
            };
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
