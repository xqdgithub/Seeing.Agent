using AntDesign;
using Seeing.Agent.Commands;
using Seeing.Agent.WebUI.Models;
using Seeing.Agent.WebUI.State;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 命令列表服务 - 从后端获取命令列表并提供给前端组件
/// </summary>
public class CommandListService
{
    private readonly ICommandRegistry _commandRegistry;

    public CommandListService(ICommandRegistry commandRegistry)
    {
        _commandRegistry = commandRegistry;
    }

    /// <summary>
    /// 获取所有可用命令（从后端 CommandRegistry）
    /// </summary>
    public List<CommandItemViewModel> GetAvailableCommands(SessionState? session = null)
    {
        var metadata = _commandRegistry.GetAllMetadata()
            .Where(c => !c.IsHidden)
            .OrderBy(c => c.SortOrder)
            .ThenBy(c => c.Name);

        var commands = metadata.Select(m => new CommandItemViewModel
        {
            Value = $"/{m.Name}",
            Name = m.Name,
            Description = m.Description,
            IconType = GetIconForCategory(m.Category),
            Category = m.Category.ToString(),
            Aliases = m.Aliases,
            Priority = m.SortOrder,
            IsBuiltIn = true,
            Keybind = GetKeybindForCommand(m.Name),
            IsDisabled = !CanExecute(m.Name, session),
            DisabledReason = GetDisabledReason(m.Name, session)
        }).ToList();

        return commands;
    }

    /// <summary>
    /// 检查命令是否可执行
    /// </summary>
    public bool CanExecute(string commandName, SessionState? session)
    {
        var requiresSession = new[] { "clear", "fork", "compact" };
        return !requiresSession.Contains(commandName, StringComparer.OrdinalIgnoreCase) 
               || session?.CurrentSession != null;
    }

    /// <summary>
    /// 获取禁用原因
    /// </summary>
    public string? GetDisabledReason(string commandName, SessionState? session)
    {
        if (CanExecute(commandName, session))
            return null;

        return "Requires active session";
    }

    /// <summary>
    /// 根据命令分类映射图标
    /// </summary>
    private static string GetIconForCategory(CommandCategory category) => category switch
    {
        CommandCategory.Basic => IconType.Outline.Home,
        CommandCategory.Navigation => IconType.Outline.Compass,
        CommandCategory.Agent => IconType.Outline.Robot,
        CommandCategory.Tools => IconType.Outline.Tool,
        CommandCategory.System => IconType.Outline.Setting,
        CommandCategory.Extension => IconType.Outline.Appstore,
        _ => IconType.Outline.Code
    };

    /// <summary>
    /// 为常用命令提供快捷键提示
    /// </summary>
    private static string? GetKeybindForCommand(string name) => name.ToLowerInvariant() switch
    {
        "new" => "Ctrl+Shift+N",
        "agent" => "Ctrl+.",
        "model" => "Ctrl+'",
        "mcp" => "Ctrl+;",
        "terminal" => "Ctrl+`",
        _ => null
    };
}