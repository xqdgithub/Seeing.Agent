using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Commands;
using Seeing.Agent.Commands.Attributes;
using Seeing.Agent.Compression;
using Seeing.Agent.App.Execution;
using Seeing.Agent.Core.Models;
using Seeing.Session.Core;

namespace Seeing.Agent.App.Commands.BuiltIn;

/// <summary>
/// 内置命令提供者 - 提供基础命令
/// </summary>
[CommandProvider]
public class BuiltInCommands
{
    private readonly ISessionManager _sessionManager;
    private readonly ICommandRegistry _commandRegistry;
    private readonly CompactionRunner _compactionRunner;

    public BuiltInCommands(
        ISessionManager sessionManager,
        ICommandRegistry commandRegistry,
        CompactionRunner compactionRunner)
    {
        _sessionManager = sessionManager;
        _commandRegistry = commandRegistry;
        _compactionRunner = compactionRunner;
    }

    /// <summary>
    /// /new - 创建新会话
    /// </summary>
    [Command(
        "创建一个新的会话",
        Name = "new",
        Usage = "/new [title]",
        Category = CommandCategory.Basic,
        Aliases = new[] { "n" },
        Type = CommandType.System,
        SupportedRuntimes = new[] { AgentRuntime.Native })]
    public CommandResult NewSession(CommandContext context, string args = "")
    {
        var title = args.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            title = $"New Session {DateTime.Now:yyyy-MM-dd HH:mm}";
        }

        // 新会话 ID 由调用方创建，这里只标记需要新建
        return CommandResult.Ok($"会话已创建: {title}", shouldContinue: false)
            .WithNavigation($"/session/new?title={Uri.EscapeDataString(title)}");
    }

    /// <summary>
    /// /clear - 清除当前会话消息
    /// </summary>
    [Command(
        "清除当前会话的消息历史",
        Name = "clear",
        Usage = "/clear",
        Category = CommandCategory.Basic,
        Aliases = new[] { "cls" },
        Type = CommandType.System,
        SupportedRuntimes = new[] { AgentRuntime.Native })]
    public async Task<CommandResult> ClearSession(CommandContext context, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(context.SessionId))
        {
            return CommandResult.Fail("No active session");
        }

        // 获取并清除会话消息
        var session = _sessionManager.Get(context.SessionId);
        if (session == null)
        {
            return CommandResult.Fail($"Session not found: {context.SessionId}");
        }

        session.ClearMessages();
        await _sessionManager.SaveAsync(context.SessionId);

        return CommandResult.Ok("会话历史已清除", needsRefresh: true, shouldContinue: false);
    }

    /// <summary>
    /// /help - 显示帮助信息
    /// </summary>
    [Command(
        "显示可用命令列表",
        Name = "help",
        Usage = "/help [command]",
        Category = CommandCategory.Basic,
        Aliases = new[] { "h", "?" },
        Type = CommandType.System,
        SupportedRuntimes = new[] { AgentRuntime.Native })]
    public CommandResult ShowHelp(string args = "")
    {
        var commands = _commandRegistry.GetAllMetadata()
            .Where(c => !c.IsHidden)
            .OrderBy(c => c.SortOrder)
            .ThenBy(c => c.Name)
            .ToList();

        if (!string.IsNullOrWhiteSpace(args))
        {
            // 显示特定命令的详细帮助
            var cmdName = args.TrimStart('/');
            var cmd = commands.FirstOrDefault(c =>
                c.Name.Equals(cmdName, StringComparison.OrdinalIgnoreCase) ||
                c.Aliases.Any(a => a.Equals(cmdName, StringComparison.OrdinalIgnoreCase)));

            if (cmd == null)
            {
                return CommandResult.Fail($"Command not found: {cmdName}");
            }

            var details = $"/{cmd.Name}";
            if (cmd.Aliases.Length > 0)
            {
                details += $" (aliases: {string.Join(", ", cmd.Aliases.Select(a => "/" + a))})";
            }
            details += $"\n\n{cmd.Description}";
            if (!string.IsNullOrEmpty(cmd.Usage))
            {
                details += $"\n\nUsage: {cmd.Usage}";
            }
            if (cmd.Examples.Length > 0)
            {
                details += "\n\nExamples:";
                foreach (var example in cmd.Examples)
                {
                    details += $"\n  {example}";
                }
            }

            return CommandResult.Ok(details, shouldContinue: false);
        }

        // 显示命令列表，按分类分组
        var groups = commands
            .GroupBy(c => c.Category)
            .OrderBy(g => g.Key);

        var help = "**Available Commands**\n\n";

        foreach (var group in groups)
        {
            help += $"**{group.Key}**\n";
            foreach (var cmd in group)
            {
                var aliases = cmd.Aliases.Length > 0
                    ? $" ({string.Join(", ", cmd.Aliases)})"
                    : "";
                help += $"  /{cmd.Name}{aliases} - {cmd.Description}\n";
            }
            help += "\n";
        }

        help += "Type /help [command] for detailed information.";

        return CommandResult.Ok(help, shouldContinue: false);
    }

    /// <summary>
    /// /compact - 压缩会话历史
    /// </summary>
    [Command(
        "压缩会话历史，保留最近的消息",
        Name = "compact",
        Usage = "/compact [count]",
        Category = CommandCategory.Basic,
        Type = CommandType.System,
        SupportedRuntimes = new[] { AgentRuntime.Native })]
    public async Task<CommandResult> CompactHistory(CommandContext context, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(context.SessionId))
        {
            return CommandResult.Fail("No active session");
        }

        // 统一压缩入口（Started 先于 Delta 发布，UI 进度时序正确）
        var outcome = await _compactionRunner.RunAsync(
            context.SessionId,
            reason: "manual",
            cancellationToken: ct);

        if (!outcome.Success)
        {
            return CommandResult.Fail(outcome.ErrorMessage ?? "压缩失败");
        }

        // 根因修复：shouldContinue:false 终止 Agent 循环（CommandResult.Ok 默认 shouldContinue:true）
        return CommandResult.Ok(
            $"Compacted history: removed {outcome.MessagesRemoved} messages",
            needsRefresh: true,
            shouldContinue: false);
    }

    /// <summary>
    /// /exit - 退出应用
    /// </summary>
    [Command(
        "退出应用",
        Name = "exit",
        Usage = "/exit",
        Category = CommandCategory.System,
        Aliases = new[] { "quit", "q" },
        Type = CommandType.System,
        SupportedRuntimes = new[] { AgentRuntime.Native })]
    public CommandResult Exit()
    {
        return CommandResult.Exit("Goodbye!");
    }
}