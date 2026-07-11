using Seeing.Agent.Commands;
using Seeing.Agent.Commands.Attributes;
using Seeing.Agent.Skills;

namespace Seeing.Agent.App.Commands;

/// <summary>
/// Skill 命令提供者 - 提供 /skill 命令
/// </summary>
[CommandProvider]
public class SkillCommands
{
    private readonly SkillManager _skillManager;

    public SkillCommands(SkillManager skillManager)
    {
        _skillManager = skillManager;
    }

    /// <summary>
    /// /skill - 加载并使用技能
    /// </summary>
    [Command(
        "加载技能作为上下文",
        Name = "skill",
        Usage = "/skill <skill-name> [args]",
        Category = CommandCategory.Tools,
        Type = CommandType.Skill)]
    public Task<CommandResult> LoadSkill(CommandContext context, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(context.Arguments))
        {
            // 列出可用技能
            var skills = _skillManager.GetAllSkillInfos().Values.ToList();
            if (skills.Count == 0)
            {
                return Task.FromResult(CommandResult.Ok("No skills available. Add skills to .agents/skills/ or ~/.agents/skills/"));
            }

            var list = "**Available Skills**\n\n";
            foreach (var skill in skills.OrderBy(s => s.Name))
            {
                list += $"- **{skill.Name}**: {skill.Description}\n";
            }
            list += "\nUse `/skill <name>` to load a skill.";

            return Task.FromResult(CommandResult.Ok(list));
        }

        // 解析技能名称和参数
        var parts = context.Arguments.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var skillName = parts[0];
        var skillArgs = parts.Length > 1 ? parts[1] : "";

        // 加载技能
        var skillInfo = _skillManager.GetSkillInfo(skillName);
        if (skillInfo == null)
        {
            return Task.FromResult(CommandResult.Fail($"Skill not found: {skillName}"));
        }

        // 获取技能内容
        var content = skillInfo.Content;
        if (string.IsNullOrEmpty(content))
        {
            return Task.FromResult(CommandResult.Fail($"Skill content is empty: {skillName}"));
        }

        // 如果有参数，追加到技能内容
        var expandedContent = string.IsNullOrEmpty(skillArgs)
            ? content
            : $"{content}\n\n**User Input:**\n{skillArgs}";

        // 返回展开后的内容，继续 Agent 执行
        return Task.FromResult(CommandResult.Ok($"Loaded skill: {skillName}")
            .WithExpandedContent(expandedContent, $"/skill {skillName}"));
    }

    /// <summary>
    /// /skills - 列出所有技能
    /// </summary>
    [Command(
        "列出所有可用技能",
        Name = "skills",
        Usage = "/skills",
        Category = CommandCategory.Tools,
        Aliases = new[] { "ls-skills" },
        Type = CommandType.System)]
    public CommandResult ListSkills()
    {
        var skills = _skillManager.GetAllSkillInfos().Values.ToList();
        if (skills.Count == 0)
        {
            return CommandResult.Ok("No skills available.");
        }

        var list = "**Available Skills**\n\n";
        foreach (var skill in skills.OrderBy(s => s.Name))
        {
            list += $"- **{skill.Name}**: {skill.Description}\n";
        }

        return CommandResult.Ok(list);
    }
}
