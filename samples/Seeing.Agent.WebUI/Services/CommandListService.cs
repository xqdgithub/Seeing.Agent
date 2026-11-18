using AntDesign;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.WebUI.Models;
using Seeing.Agent.WebUI.State;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 命令列表服务 - 从后端获取命令列表并提供给前端组件
/// </summary>
public class CommandListService
{
    private readonly ICommandRegistry _commandRegistry;
    private readonly IAgentRegistry _agentRegistry;

    public CommandListService(ICommandRegistry commandRegistry, IAgentRegistry agentRegistry)
    {
        _commandRegistry = commandRegistry;
        _agentRegistry = agentRegistry;
    }

    /// <summary>
    /// 获取所有可用命令（从后端 CommandRegistry），根据当前 Agent 的 Runtime 过滤。
    /// <para>排序即面板展示顺序：先按分组（内置 → 技能 → 自定义 → 扩展），
    /// 组内按 <c>SortOrder</c> 再按名称。</para>
    /// </summary>
    public List<CommandItemViewModel> GetAvailableCommands(SessionState? session = null)
    {
        // 获取当前 Agent 的 Runtime
        var currentRuntime = GetCurrentRuntime(session);

        // 按 Runtime 过滤命令
        var commands = _commandRegistry.GetCommandsByRuntime(currentRuntime)
            .Select(c => c.Metadata)
            .Where(c => !c.IsHidden)
            .Select(m => CreateViewModel(m, session))
            .OrderBy(c => c.GroupOrder)
            .ThenBy(c => c.Priority)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return commands;
    }

    /// <summary>命令元数据 → 视图模型（分组与分类徽标在这里一次算清，组件只负责渲染）。</summary>
    private CommandItemViewModel CreateViewModel(CommandMetadata metadata, SessionState? session)
    {
        var group = ResolveGroup(metadata);
        return new CommandItemViewModel
        {
            Value = $"/{metadata.Name}",
            Name = metadata.Name,
            Description = metadata.Description,
            IconType = ResolveIcon(metadata, group),
            Category = metadata.Category.ToString(),
            Aliases = metadata.Aliases,
            Priority = metadata.SortOrder,
            IsSkill = metadata.Type == CommandType.Skill,
            IsCustom = group == CommandGroupKind.Custom,
            IsBuiltIn = group == CommandGroupKind.BuiltIn,
            Source = metadata.Source,
            Group = group,
            GroupTitle = UiText.CommandGroupTitle(group),
            Keybind = GetKeybindForCommand(metadata.Name),
            IsDisabled = !CanExecute(metadata.Name, session),
            DisabledReason = GetDisabledReason(metadata.Name, session)
        };
    }

    /// <summary>
    /// 命令分组判定（唯一入口）：类型优先，其次来源，最后回落到内置。
    /// </summary>
    private static CommandGroupKind ResolveGroup(CommandMetadata metadata)
    {
        if (metadata.Type == CommandType.Skill)
            return CommandGroupKind.Skill;

        if (string.Equals(metadata.Source, "mcp", StringComparison.OrdinalIgnoreCase)
            || metadata.Category == CommandCategory.Extension)
        {
            return CommandGroupKind.Extension;
        }

        // Markdown 命令的 Source 是文件路径（CommandDiscovery 写入 filePath）
        if (IsFileSource(metadata.Source))
            return CommandGroupKind.Custom;

        return CommandGroupKind.BuiltIn;
    }

    private static bool IsFileSource(string? source)
        => !string.IsNullOrWhiteSpace(source)
           && (source.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
               || source.Contains(Path.DirectorySeparatorChar)
               || source.Contains(Path.AltDirectorySeparatorChar));

    /// <summary>
    /// 获取当前 Agent 的 Runtime
    /// </summary>
    private AgentRuntime GetCurrentRuntime(SessionState? session)
    {
        if (session == null || string.IsNullOrEmpty(session.SelectedAgent))
            return AgentRuntime.Native;

        try
        {
            var agent = _agentRegistry.GetAgentAsync(session.SelectedAgent).GetAwaiter().GetResult();
            return agent?.Runtime ?? AgentRuntime.Native;
        }
        catch
        {
            return AgentRuntime.Native;
        }
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
    /// 根据命令分组 / 分类映射图标
    /// </summary>
    private static string ResolveIcon(CommandMetadata metadata, CommandGroupKind group) => group switch
    {
        CommandGroupKind.Skill => IconType.Outline.Book,
        CommandGroupKind.Custom => IconType.Outline.FileText,
        CommandGroupKind.Extension => IconType.Outline.Appstore,
        _ => GetIconForCategory(metadata.Category)
    };

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
