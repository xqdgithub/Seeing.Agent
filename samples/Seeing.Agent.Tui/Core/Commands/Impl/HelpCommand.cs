using Seeing.Agent.Commands;

namespace Seeing.Agent.Tui.Core.Commands.Impl;

/// <summary>
/// 帮助命令 - 动态生成帮助信息
/// </summary>
public class HelpCommand : ICommand
{
    private readonly ICommandRegistry _registry;

    public HelpCommand(ICommandRegistry registry)
    {
        _registry = registry;
    }

    public CommandMetadata Metadata => new()
    {
        Name = "help",
        Aliases = new[] { "?" },
        Description = "显示帮助信息",
        Usage = "/help 或 /?",
        Category = CommandCategory.Basic,
        SortOrder = 1
    };

    public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        var commands = _registry.GetAllCommands()
            .Where(c => !c.Metadata.IsHidden)
            .OrderBy(c => c.Metadata.SortOrder)
            .ToList();

        var lines = new List<string>
        {
            "═══════════ Seeing.Agent TUI 帮助 ═══════════",
            ""
        };

        // 按分类组织
        var categories = new[]
        {
            (CommandCategory.Basic, "基本命令"),
            (CommandCategory.Navigation, "消息导航"),
            (CommandCategory.Agent, "Agent 管理"),
            (CommandCategory.Tools, "工具与技能"),
            (CommandCategory.System, "系统管理"),
            (CommandCategory.Extension, "扩展命令"),
            (CommandCategory.Other, "其他命令")
        };

        foreach (var (category, title) in categories)
        {
            var categoryCommands = commands.Where(c => c.Metadata.Category == category).ToList();
            if (categoryCommands.Count == 0) continue;

            lines.Add($"{title}:");
            foreach (var cmd in categoryCommands)
            {
                var aliases = cmd.Metadata.Aliases.Length > 0
                    ? $" (别名: {string.Join(", ", cmd.Metadata.Aliases.Select(a => "/" + a))})"
                    : "";
                lines.Add($"   /{cmd.Metadata.Name}{aliases} - {cmd.Metadata.Description}");
            }
            lines.Add("");
        }

        lines.Add("提示: Tab 切换多行/单行，Enter 发送");

        return Task.FromResult(CommandResult.Ok(string.Join("\n", lines)));
    }
}