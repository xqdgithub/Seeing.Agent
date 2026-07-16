using System.Text;
using System.Text.RegularExpressions;
using Seeing.Agent.Commands;
using Seeing.Agent.Commands.Attributes;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
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
        Type = CommandType.Skill,
        SupportedRuntimes = new[] { AgentRuntime.Native })]
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

        // 获取技能信息
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

        // 处理模板并修改 Input
        context.Input = SkillTemplateProcessor.Process(content, skillArgs, skillName);

        return Task.FromResult(CommandResult.Ok($"Loaded skill: {skillName}"));
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
        Type = CommandType.System,
        SupportedRuntimes = new[] { AgentRuntime.Native })]
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

/// <summary>
/// Skill 模板处理器 - 处理 skill 内容中的占位符
/// </summary>
public static class SkillTemplateProcessor
{
    // 匹配 $1, $2, $3 等占位符
    private static readonly Regex PlaceholderRegex = new(@"\$(\d+)", RegexOptions.Compiled);
    // 匹配 $ARGUMENTS 占位符
    private static readonly Regex ArgumentsRegex = new(@"\$ARGUMENTS", RegexOptions.Compiled);
    
    // 参数长度阈值：超过此长度视为"长参数"，使用简化输出
    private const int LongArgumentThreshold = 200;

    /// <summary>
    /// 处理 skill 模板内容
    /// </summary>
    /// <param name="template">skill 内容模板</param>
    /// <param name="arguments">用户输入的参数</param>
    /// <param name="skillName">skill 名称（用于标记）</param>
    /// <returns>处理后的内容</returns>
    public static string Process(string template, string arguments, string skillName = "skill")
    {
        if (string.IsNullOrEmpty(template))
            return template;

        // 解析参数（按空格分割，支持引号）
        var args = ParseArguments(arguments);
        
        // 检查模板中是否有占位符
        var placeholderMatches = PlaceholderRegex.Matches(template);
        var hasArgumentsPlaceholder = template.Contains("$ARGUMENTS");
        var hasPlaceholders = placeholderMatches.Count > 0 || hasArgumentsPlaceholder;

        // 如果没有占位符，使用明确标记区分 skill 内容和用户输入
        if (!hasPlaceholders)
        {
            var baseContent = $"<skill_content name=\"{skillName}\">\n{template.Trim()}\n</skill_content>";
            
            if (!string.IsNullOrWhiteSpace(arguments))
            {
                return $"{baseContent}\n\n<user_input>\n{arguments.Trim()}\n</user_input>";
            }
            return baseContent;
        }

        // 有占位符：判断参数长度
        var isLongArgument = arguments.Length > LongArgumentThreshold;
        
        // 执行替换
        var resolvedContent = PerformReplacement(template, args, arguments);
        
        // 长参数：简化输出，只显示最终结果
        if (isLongArgument)
        {
            return $"<skill_content name=\"{skillName}\" parameters_applied=\"true\">\n{resolvedContent}\n</skill_content>";
        }
        
        // 短参数：显示完整上下文（模板 + 参数 + 结果）
        var sb = new StringBuilder();
        sb.AppendLine($"<skill_content name=\"{skillName}\">");
        
        // 显示原始模板
        sb.AppendLine("<template>");
        sb.AppendLine(template.Trim());
        sb.AppendLine("</template>");
        
        // 显示用户提供的参数
        if (args.Count > 0 || !string.IsNullOrWhiteSpace(arguments))
        {
            sb.AppendLine();
            sb.AppendLine("<arguments>");
            if (args.Count > 0)
            {
                for (int i = 0; i < args.Count; i++)
                {
                    sb.AppendLine($"  ${i + 1} = {args[i]}");
                }
            }
            if (hasArgumentsPlaceholder && !string.IsNullOrWhiteSpace(arguments))
            {
                sb.AppendLine($"  $ARGUMENTS = {arguments.Trim()}");
            }
            sb.AppendLine("</arguments>");
        }
        
        // 显示替换结果
        sb.AppendLine();
        sb.AppendLine("<resolved>");
        sb.AppendLine(resolvedContent);
        sb.AppendLine("</resolved>");
        sb.AppendLine("</skill_content>");
        
        return sb.ToString();
    }

    /// <summary>
    /// 执行占位符替换
    /// </summary>
    private static string PerformReplacement(string template, List<string> args, string rawArguments)
    {
        // 找出最大的占位符索引
        int maxIndex = 0;
        var placeholderMatches = PlaceholderRegex.Matches(template);
        foreach (Match match in placeholderMatches)
        {
            if (int.TryParse(match.Groups[1].Value, out var index) && index > maxIndex)
            {
                maxIndex = index;
            }
        }

        // 替换 $1, $2, ... 占位符
        var result = PlaceholderRegex.Replace(template, match =>
        {
            var index = int.Parse(match.Groups[1].Value);
            var argIndex = index - 1; // $1 对应 args[0]
            
            if (argIndex >= args.Count)
                return "";
            
            // 最后一个占位符获取剩余所有参数
            if (index == maxIndex && argIndex < args.Count)
            {
                return string.Join(" ", args.Skip(argIndex));
            }
            
            return argIndex < args.Count ? args[argIndex] : "";
        });

        // 替换 $ARGUMENTS 占位符
        result = ArgumentsRegex.Replace(result, rawArguments);

        return result.Trim();
    }

    /// <summary>
    /// 解析参数（支持引号）
    /// </summary>
    private static List<string> ParseArguments(string arguments)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(arguments))
            return result;

        var current = "";
        var inQuotes = false;
        var quoteChar = '\0';

        foreach (var c in arguments)
        {
            if (inQuotes)
            {
                if (c == quoteChar)
                {
                    inQuotes = false;
                }
                else
                {
                    current += c;
                }
            }
            else
            {
                if (c == '"' || c == '\'')
                {
                    inQuotes = true;
                    quoteChar = c;
                }
                else if (char.IsWhiteSpace(c))
                {
                    if (current.Length > 0)
                    {
                        result.Add(current);
                        current = "";
                    }
                }
                else
                {
                    current += c;
                }
            }
        }

        if (current.Length > 0)
            result.Add(current);

        return result;
    }
}

/// <summary>
/// 动态 Skill 命令 - 为每个已注册的 skill 自动创建命令（Native 版本）
/// </summary>
public class DynamicSkillCommand : ICommand
{
    private readonly SkillManager _skillManager;
    private readonly string _skillName;
    private readonly SkillInfo _skillInfo;

    public CommandMetadata Metadata { get; }

    public DynamicSkillCommand(SkillManager skillManager, SkillInfo skillInfo)
    {
        _skillManager = skillManager;
        _skillName = skillInfo.Name;
        _skillInfo = skillInfo;
        
        Metadata = new CommandMetadata
        {
            Name = skillInfo.Name,
            Description = skillInfo.Description,
            Usage = $"/{skillInfo.Name} [args]",
            Category = CommandCategory.Tools,
            Type = CommandType.Skill,
            IsHidden = false,
            SortOrder = 50,
            SupportedRuntimes = new[] { AgentRuntime.Native }
        };
    }

    public async Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken = default)
    {
        var content = _skillInfo.Content;
        if (string.IsNullOrEmpty(content))
        {
            return CommandResult.Fail($"Skill content is empty: {_skillName}");
        }

        // 使用模板处理器处理内容，修改 Input
        context.Input = SkillTemplateProcessor.Process(content, context.Arguments ?? "", _skillName);

        return CommandResult.Ok($"Loaded skill: {_skillName}");
    }
}

/// <summary>
/// ACP 动态 Skill 命令 - 透传给 ACP 后端
/// </summary>
public class AcpDynamicSkillCommand : ICommand
{
    public CommandMetadata Metadata { get; }

    public AcpDynamicSkillCommand(string skillName, string? description = null)
    {
        Metadata = new CommandMetadata
        {
            Name = skillName,
            Description = description ?? $"ACP 透传: {skillName}",
            Usage = $"/{skillName} [args]",
            Category = CommandCategory.Tools,
            Type = CommandType.Skill,
            SupportedRuntimes = new[] { AgentRuntime.AcpPassthrough }
        };
    }

    public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken = default)
    {
        // 透传，不修改历史，继续执行 Agent
        return Task.FromResult(CommandResult.Ok(shouldContinue: true));
    }
}
