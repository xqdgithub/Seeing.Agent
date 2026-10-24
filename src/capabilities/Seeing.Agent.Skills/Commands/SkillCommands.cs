using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Abstractions.Skills;
using Seeing.Agent.Skills.Catalog;
using Seeing.Session.Core;
using System.Text;
using System.Text.RegularExpressions;

namespace Seeing.Agent.Skills.Commands;

/// <summary>
/// /skill — 加载并使用技能
/// </summary>
public sealed class SkillLoadCommand : ICommand
{
    private readonly ISkillCatalog _catalog;
    private readonly ISessionManager _sessionManager;

    public SkillLoadCommand(ISkillCatalog catalog, ISessionManager sessionManager)
    {
        _catalog = catalog;
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
        var snapshot = SkillCommandCatalogView.Resolve(context, _catalog);

        if (string.IsNullOrWhiteSpace(context.Arguments))
        {
            // 清单文本只有 SkillCatalogFormatter 一个出口（与基线 / 工具提示共用）
            return CommandResult.Ok(
                SkillCatalogFormatter.RenderList(SkillCommandCatalogView.EnabledEntries(snapshot)));
        }

        var parts = context.Arguments.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var skillName = parts[0];
        var skillArgs = parts.Length > 1 ? parts[1] : "";

        var entry = SkillCommandCatalogView.Find(snapshot, _catalog, skillName);
        if (entry is null)
        {
            return CommandResult.Fail(
                $"Skill not found: {skillName}\n\n{SkillCatalogFormatter.RenderList(SkillCommandCatalogView.EnabledEntries(snapshot))}");
        }

        if (!SkillCommandCatalogView.IsEnabled(snapshot, _catalog, entry.Info.Name))
        {
            return CommandResult.Fail($"Skill '{entry.Info.Name}' is disabled. Enable it in the Skills settings.");
        }

        var content = entry.Info.Content;
        if (string.IsNullOrEmpty(content))
        {
            return CommandResult.Fail($"Skill content is empty: {skillName}");
        }

        var expanded = SkillTemplateProcessor.Process(content, skillArgs, skillName);
        var session = await _sessionManager.GetOrLoadAsync(context.SessionId, cancellationToken);

        // 定位命令消息：按命令原文匹配对应 user 消息。
        // 新架构下用户消息先由 DeliverAsync 落盘、项目指令随后注入，命令消息不再是末条，
        // 盲取 [^1] 会误改指令消息并残留命令文本（透传 LLM）。
        if (!SkillCommandMessageLocator.HasMatch(session, context.Input))
        {
            return CommandResult.Fail(
                "/skill 需在用户消息之后调用：当前会话未找到对应的用户命令消息，无法注入技能内容");
        }

        // 走 SessionManager API 持久化，避免只改内存导致刷新后丢失
        await _sessionManager.UpdateSessionAsync(
            context.SessionId,
            s => SkillCommandMessageLocator.Replace(s, context.Input, expanded),
            cancellationToken);

        return new CommandResult
        {
            Success = true,
            Message = $"Loaded skill: {skillName}",
            NeedsRefresh = true,
            ShouldContinue = true,
            RemoveCommandMessage = false
        };
    }
}

/// <summary>
/// /skills — 列出所有技能
/// </summary>
public sealed class SkillsListCommand : ICommand
{
    private readonly ISkillCatalog _catalog;

    public SkillsListCommand(ISkillCatalog catalog)
    {
        _catalog = catalog;
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
        => Task.FromResult(CommandResult.Ok(
            SkillCatalogFormatter.RenderList(
                SkillCommandCatalogView.EnabledEntries(SkillCommandCatalogView.Resolve(context, _catalog)))));
}

/// <summary>
/// 命令侧读取技能目录的<b>唯一接缝</b>：优先执行级钉住快照（<see cref="CommandContext.Skill"/>），
/// 仅在宿主未接线时（旧宿主 / 命令单测）才退回注入目录的当前态。
/// <para>只有本类允许触碰 <see cref="ISkillCatalog.Current"/>：命令实现（/skill、/skills、/&lt;技能名&gt;）
/// 一律经此处解析，保证「提示词清单 / skill 工具 / 命令」三者同源，不会在一次执行内出现两个版本。</para>
/// </summary>
internal static class SkillCommandCatalogView
{
    /// <summary>本次命令应使用的快照；都取不到时返回 null（目录不可用）。</summary>
    public static SkillCatalogSnapshot? Resolve(CommandContext context, ISkillCatalog? injectedCatalog)
        => context.Skill
           ?? injectedCatalog?.Current
           // 兜底接缝：宿主只填了 Services 时按名取一次（不缓存，命令不得持有目录副本）
           ?? (context.Services?.GetService<ISkillCatalog>())?.Current;

    /// <summary>可用技能条目（唯一投影；禁用项不进入清单）。</summary>
    public static IReadOnlyList<SkillCatalogEntry> EnabledEntries(SkillCatalogSnapshot? snapshot)
        => snapshot is null ? [] : SkillCatalogProjection.EnabledEntries(snapshot);

    /// <summary>按名查找条目（优先钉住快照）。</summary>
    public static SkillCatalogEntry? Find(
        SkillCatalogSnapshot? snapshot,
        ISkillCatalog? injectedCatalog,
        string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        if (snapshot is not null)
        {
            return snapshot.Entries.FirstOrDefault(entry =>
                string.Equals(entry.Info.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        return injectedCatalog?.Find(name);
    }

    /// <summary>技能是否可用（存在且未被任何层级禁用）。</summary>
    public static bool IsEnabled(
        SkillCatalogSnapshot? snapshot,
        ISkillCatalog? injectedCatalog,
        string name)
    {
        if (snapshot is not null)
        {
            return Find(snapshot, injectedCatalog, name) is not null
                   && !snapshot.DisabledNames.Contains(name);
        }

        return injectedCatalog?.IsEnabled(name) == true;
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
/// 定位 skill 命令对应的用户消息：按命令原文（<see cref="CommandContext.Input"/>）匹配最后一条 user 消息。
/// <para>新架构下用户消息先由 <c>SessionRuntime.DeliverAsync</c> 落盘、项目指令随后由
/// <c>ExecutionRunner</c> 注入，命令消息不再是会话末条。盲取末条会误改指令消息并残留命令文本
/// （命令文本原样透传给 LLM）。</para>
/// </summary>
internal static class SkillCommandMessageLocator
{
    /// <summary>是否存在与命令原文匹配的 user 消息。</summary>
    public static bool HasMatch(SessionData session, string? input)
        => FindMessage(session, input) is not null;

    /// <summary>将匹配的命令 user 消息内容替换为 <paramref name="content"/>。</summary>
    public static void Replace(SessionData session, string? input, string content)
    {
        var message = FindMessage(session, input);
        if (message is not null)
            message.Content = content;
    }

    /// <summary>按内容匹配最后一条 user 消息（命中当前命令）；未命中返回 null。</summary>
    private static SessionMessage? FindMessage(SessionData session, string? input)
    {
        if (session is null || string.IsNullOrEmpty(input))
            return null;

        var messages = session.Messages;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var message = messages[i];
            if (string.Equals(message.Role, MessageRole.User, StringComparison.OrdinalIgnoreCase)
                && string.Equals(message.Content, input, StringComparison.Ordinal))
            {
                return message;
            }
        }

        return null;
    }
}

/// <summary>
/// 动态 Skill 命令 - 为每个已注册的 skill 自动创建命令（Native 版本）
/// <para>只持有技能名：正文与元数据在执行时从 <see cref="ISkillCatalog"/> 现取，
/// 避免第二份 <see cref="SkillInfo"/> 副本与目录漂移。</para>
/// </summary>
public sealed class DynamicSkillCommand : ICommand
{
    private readonly ISessionManager _sessionManager;
    private readonly string _skillName;

    public CommandMetadata Metadata { get; }

    public DynamicSkillCommand(ISessionManager sessionManager, string skillName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillName);

        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _skillName = skillName;

        Metadata = new CommandMetadata
        {
            Name = skillName,
            Description = $"加载技能：{skillName}",
            Usage = $"/{skillName} [args]",
            Category = CommandCategory.Tools,
            Type = CommandType.Skill,
            IsHidden = false,
            SortOrder = 50,
            SupportedRuntimes = [AgentRuntime.Native]
        };
    }

    public async Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken = default)
    {
        // 执行时解析：命令不得持有目录副本。解析走唯一接缝（钉住快照优先），
        // 不直接读目录当前态——否则同一次执行内清单 / 工具 / 命令会出现两个版本。
        var snapshot = SkillCommandCatalogView.Resolve(context, injectedCatalog: null);
        if (snapshot is null)
        {
            return CommandResult.Fail(
                $"技能目录不可用，无法执行 /{_skillName}：请检查 Skills 模块是否已加载。");
        }

        var entry = SkillCommandCatalogView.Find(snapshot, injectedCatalog: null, _skillName);
        if (entry is null)
        {
            return CommandResult.Fail($"Skill not found: {_skillName}");
        }

        if (!SkillCommandCatalogView.IsEnabled(snapshot, injectedCatalog: null, entry.Info.Name))
        {
            return CommandResult.Fail(
                $"Skill '{entry.Info.Name}' is disabled. Enable it in the Skills settings.");
        }

        var content = entry.Info.Content;
        if (string.IsNullOrEmpty(content))
        {
            return CommandResult.Fail($"Skill content is empty: {_skillName}");
        }

        var expanded = SkillTemplateProcessor.Process(content, context.Arguments ?? "", _skillName);
        var session = await _sessionManager.GetOrLoadAsync(context.SessionId, cancellationToken);

        // 定位命令消息：按命令原文匹配对应 user 消息（新架构下命令消息不再是末条）。
        if (!SkillCommandMessageLocator.HasMatch(session, context.Input))
        {
            return CommandResult.Fail(
                $"/{_skillName} 需在用户消息之后调用：当前会话未找到对应的用户命令消息，无法注入技能内容");
        }

        // 走 SessionManager API 持久化，避免只改内存导致刷新后丢失
        await _sessionManager.UpdateSessionAsync(
            context.SessionId,
            s => SkillCommandMessageLocator.Replace(s, context.Input, expanded),
            cancellationToken);

        return new CommandResult
        {
            Success = true,
            Message = $"Loaded skill: {_skillName}",
            NeedsRefresh = true,
            ShouldContinue = true,
            RemoveCommandMessage = false
        };
    }
}
