using System.Text;
using System.Text.RegularExpressions;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Abstractions.Skills;
using Seeing.Session.Core;

namespace Seeing.Agent.Skills.Commands;

/// <summary>
/// /skill — 加载并使用技能
/// </summary>
public sealed class SkillLoadCommand : ICommand
{
    private readonly ISkillManager _skillManager;
    private readonly ISessionManager _sessionManager;

    public SkillLoadCommand(ISkillManager skillManager, ISessionManager sessionManager)
    {
        _skillManager = skillManager;
        _sessionManager = sessionManager;
    }

    public CommandMetadata Metadata { get; } = new()
    {
        Name = "skill",
        Description = "加载技能作为上下文",
        Usage = "/skill <skill-name> [args]",
        Category = CommandCategory.Tools,
        Type = CommandType.Skill,
        SupportedRuntimes = [AgentRuntime.Native]
    };

    public async Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.Arguments))
        {
            var skills = _skillManager.GetAllSkillInfos().Values.ToList();
            if (skills.Count == 0)
            {
                return CommandResult.Ok("No skills available. Add skills to .agents/skills/ or ~/.agents/skills/");
            }

            var list = "**Available Skills**\n\n";
            foreach (var skill in skills.OrderBy(s => s.Name))
            {
                list += $"- **{skill.Name}**: {skill.Description}\n";
            }
            list += "\nUse `/skill <name>` to load a skill.";

            return CommandResult.Ok(list);
        }

        var parts = context.Arguments.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var skillName = parts[0];
        var skillArgs = parts.Length > 1 ? parts[1] : "";

        var skillInfo = _skillManager.GetSkillInfo(skillName);
        if (skillInfo == null)
        {
            return CommandResult.Fail($"Skill not found: {skillName}");
        }

        var content = skillInfo.Content;
        if (string.IsNullOrEmpty(content))
        {
            return CommandResult.Fail($"Skill content is empty: {skillName}");
        }

        var expanded = SkillTemplateProcessor.Process(content, skillArgs, skillName);
        var session = await _sessionManager.GetOrLoadAsync(context.SessionId, cancellationToken);
        if (session.Messages.Count > 0 && session.Messages[^1].Role == MessageRole.User)
        {
            session.Messages[^1].Content = expanded;
        }

        return new CommandResult
        {
            Success = true,
            Message = $"Loaded skill: {skillName}",
            NeedsRefresh = true,
            RemoveCommandMessage = false
        };
    }
}

/// <summary>
/// /skills — 列出所有技能
/// </summary>
public sealed class SkillsListCommand : ICommand
{
    private readonly ISkillManager _skillManager;

    public SkillsListCommand(ISkillManager skillManager)
    {
        _skillManager = skillManager;
    }

    public CommandMetadata Metadata { get; } = new()
    {
        Name = "skills",
        Description = "列出所有可用技能",
        Usage = "/skills",
        Category = CommandCategory.Tools,
        Aliases = ["ls-skills"],
        Type = CommandType.System,
        SupportedRuntimes = [AgentRuntime.Native]
    };

    public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken = default)
    {
        var skills = _skillManager.GetAllSkillInfos().Values.ToList();
        if (skills.Count == 0)
        {
            return Task.FromResult(CommandResult.Ok("No skills available."));
        }

        var list = "**Available Skills**\n\n";
        foreach (var skill in skills.OrderBy(s => s.Name))
        {
            list += $"- **{skill.Name}**: {skill.Description}\n";
        }

        return Task.FromResult(CommandResult.Ok(list));
    }
}

/// <summary>
/// Skill 模板处理器 - 处理 skill 内容中的占位符
/// </summary>
public static class SkillTemplateProcessor
{
    private static readonly Regex PlaceholderRegex = new(@"\$(\d+)", RegexOptions.Compiled);
    private static readonly Regex ArgumentsRegex = new(@"\$ARGUMENTS", RegexOptions.Compiled);
    private const int LongArgumentThreshold = 200;

    public static string Process(string template, string arguments, string skillName = "skill")
    {
        if (string.IsNullOrEmpty(template))
            return template;

        var args = ParseArguments(arguments);

        var placeholderMatches = PlaceholderRegex.Matches(template);
        var hasArgumentsPlaceholder = template.Contains("$ARGUMENTS");
        var hasPlaceholders = placeholderMatches.Count > 0 || hasArgumentsPlaceholder;

        if (!hasPlaceholders)
        {
            var baseContent = $"<skill_content name=\"{skillName}\">\n{template.Trim()}\n</skill_content>";

            if (!string.IsNullOrWhiteSpace(arguments))
            {
                return $"{baseContent}\n\n<user_input>\n{arguments.Trim()}\n</user_input>";
            }
            return baseContent;
        }

        var isLongArgument = arguments.Length > LongArgumentThreshold;
        var resolvedContent = PerformReplacement(template, args, arguments);

        if (isLongArgument)
        {
            return $"<skill_content name=\"{skillName}\" parameters_applied=\"true\">\n{resolvedContent}\n</skill_content>";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"<skill_content name=\"{skillName}\">");

        sb.AppendLine("<template>");
        sb.AppendLine(template.Trim());
        sb.AppendLine("</template>");

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

        sb.AppendLine();
        sb.AppendLine("<resolved>");
        sb.AppendLine(resolvedContent);
        sb.AppendLine("</resolved>");
        sb.AppendLine("</skill_content>");

        return sb.ToString();
    }

    private static string PerformReplacement(string template, List<string> args, string rawArguments)
    {
        int maxIndex = 0;
        var placeholderMatches = PlaceholderRegex.Matches(template);
        foreach (Match match in placeholderMatches)
        {
            if (int.TryParse(match.Groups[1].Value, out var index) && index > maxIndex)
            {
                maxIndex = index;
            }
        }

        var result = PlaceholderRegex.Replace(template, match =>
        {
            var index = int.Parse(match.Groups[1].Value);
            var argIndex = index - 1;

            if (argIndex >= args.Count)
                return "";

            if (index == maxIndex && argIndex < args.Count)
            {
                return string.Join(" ", args.Skip(argIndex));
            }

            return argIndex < args.Count ? args[argIndex] : "";
        });

        result = ArgumentsRegex.Replace(result, rawArguments);

        return result.Trim();
    }

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
public sealed class DynamicSkillCommand : ICommand
{
    private readonly ISessionManager _sessionManager;
    private readonly string _skillName;
    private readonly SkillInfo _skillInfo;

    public CommandMetadata Metadata { get; }

    public DynamicSkillCommand(ISessionManager sessionManager, SkillInfo skillInfo)
    {
        _sessionManager = sessionManager;
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
            SupportedRuntimes = [AgentRuntime.Native]
        };
    }

    public async Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken = default)
    {
        var content = _skillInfo.Content;
        if (string.IsNullOrEmpty(content))
        {
            return CommandResult.Fail($"Skill content is empty: {_skillName}");
        }

        var expanded = SkillTemplateProcessor.Process(content, context.Arguments ?? "", _skillName);
        var session = await _sessionManager.GetOrLoadAsync(context.SessionId, cancellationToken);
        if (session.Messages.Count > 0 && session.Messages[^1].Role == MessageRole.User)
        {
            session.Messages[^1].Content = expanded;
        }

        return new CommandResult
        {
            Success = true,
            Message = $"Loaded skill: {_skillName}",
            NeedsRefresh = true,
            RemoveCommandMessage = false
        };
    }
}
