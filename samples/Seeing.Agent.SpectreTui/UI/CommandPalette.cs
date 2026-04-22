using Spectre.Console;
using Seeing.Agent.SpectreTui.Core;

namespace Seeing.Agent.SpectreTui.UI;

/// <summary>
/// 命令面板项 - 定义单个命令的属性
/// </summary>
public class CommandItem
{
    /// <summary>命令唯一标识</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>命令显示名称</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>命令描述</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>命令分组（用于组织命令）</summary>
    public string? Group { get; init; }

    /// <summary>命令执行动作</summary>
    public Action? Execute { get; init; }

    /// <summary>命令是否可用</summary>
    public bool IsEnabled { get; init; } = true;
}

/// <summary>
/// Spotlight 风格命令面板 - 使用 Spectre.Console SelectionPrompt 实现
/// 支持 Ctrl+P 打开，搜索过滤命令，显示命令名称+描述
/// </summary>
public class CommandPalette
{
    /// <summary>命令列表</summary>
    private readonly List<CommandItem> _commands = new();

    /// <summary>命令面板标题</summary>
    private const string PanelTitle = "命令面板";

    /// <summary>搜索提示文本</summary>
    private const string SearchPlaceholder = "输入搜索命令...";

    /// <summary>每页显示命令数量</summary>
    private const int PageSize = 10;

    /// <summary>
    /// 注册单个命令
    /// </summary>
    /// <param name="command">命令项</param>
    public void RegisterCommand(CommandItem command)
    {
        if (command == null || string.IsNullOrEmpty(command.Name))
            return;

        // 避免重复注册
        if (!_commands.Any(c => c.Id == command.Id || c.Name == command.Name))
        {
            _commands.Add(command);
        }
    }

    /// <summary>
    /// 批量注册命令
    /// </summary>
    /// <param name="commands">命令项集合</param>
    public void RegisterCommands(IEnumerable<CommandItem> commands)
    {
        foreach (var command in commands)
        {
            RegisterCommand(command);
        }
    }

    /// <summary>
    /// 移除命令
    /// </summary>
    /// <param name="commandId">命令ID</param>
    public bool RemoveCommand(string commandId)
    {
        var command = _commands.FirstOrDefault(c => c.Id == commandId);
        if (command != null)
        {
            _commands.Remove(command);
            return true;
        }
        return false;
    }

    /// <summary>
    /// 清空所有命令
    /// </summary>
    public void ClearCommands()
    {
        _commands.Clear();
    }

    /// <summary>
    /// 获取所有已注册命令
    /// </summary>
    public IReadOnlyList<CommandItem> GetCommands() => _commands.AsReadOnly();

    /// <summary>
    /// 显示命令面板并等待用户选择
    /// 使用 SelectionPrompt 实现 Spotlight 风格的搜索过滤
    /// </summary>
    /// <returns>是否执行了命令</returns>
    public bool Show()
    {
        // 如果没有命令，显示提示
        if (_commands.Count == 0)
        {
            AnsiConsole.MarkupLine($"[{ColorScheme.WarningColor}]没有可用的命令[/]");
            return false;
        }

        // 过滤可用命令
        var enabledCommands = _commands.Where(c => c.IsEnabled).ToList();
        if (enabledCommands.Count == 0)
        {
            AnsiConsole.MarkupLine($"[{ColorScheme.WarningColor}]所有命令当前不可用[/]");
            return false;
        }

        // 构建选择项显示文本（名称 + 描述）
        var choices = new List<string>();
        var commandMap = new Dictionary<string, CommandItem>();

        // 按分组组织命令
        var groupedCommands = enabledCommands
            .GroupBy(c => c.Group ?? "常规")
            .OrderBy(g => g.Key);

        foreach (var group in groupedCommands)
        {
            // 添加分组标题作为分隔
            if (groupedCommands.Count() > 1)
            {
                choices.Add($"── {group.Key} ──");
            }

            foreach (var command in group.OrderBy(c => c.Name))
            {
                // 显示格式: 名称 + 简短描述
                var displayText = string.IsNullOrEmpty(command.Description)
                    ? command.Name
                    : $"{command.Name} [dim]─ {command.Description}[/]";

                choices.Add(displayText);
                commandMap[displayText] = command;
            }
        }

        try
        {
            // 创建 SelectionPrompt，启用搜索功能
            var prompt = new SelectionPrompt<string>()
                .Title($"[{ColorScheme.PrimaryColor}]{PanelTitle}[/]")
                .PageSize(PageSize)
                .MoreChoicesText($"[{ColorScheme.InfoColor}](↑↓ 导航，输入搜索)[/]")
                .EnableSearch()
                .SearchPlaceholderText($"[{ColorScheme.InfoColor}]{SearchPlaceholder}[/]")
                .HighlightStyle(new Style(Color.Blue))
                .AddChoices(choices);

            // 显示选择面板
            var selected = AnsiConsole.Prompt(prompt);

            // 忽略分组分隔行
            if (selected.StartsWith("──"))
            {
                return false;
            }

            // 执行选中的命令
            if (commandMap.TryGetValue(selected, out var command) && command.Execute != null)
            {
                command.Execute.Invoke();
                return true;
            }

            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException || ex is TaskCanceledException)
        {
            // 用户取消了选择（按 Escape 或 Ctrl+C）
            return false;
        }
    }

    /// <summary>
    /// 异步显示命令面板（用于非阻塞场景）
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>是否执行了命令</returns>
    public async Task<bool> ShowAsync(CancellationToken cancellationToken = default)
    {
        // SelectionPrompt 是阻塞的，在单独线程运行
        return await Task.Run(() => Show(), cancellationToken);
    }

    /// <summary>
    /// 创建默认命令列表（示例命令）
    /// </summary>
    public static IEnumerable<CommandItem> CreateDefaultCommands()
    {
        return new[]
        {
            new CommandItem
            {
                Id = "toggle_multiline",
                Name = "切换多行模式",
                Description = "切换输入框为多行/单行模式",
                Group = "输入",
                Execute = () => AnsiConsole.MarkupLine("[green]已切换多行模式[/]")
            },
            new CommandItem
            {
                Id = "clear_history",
                Name = "清空历史记录",
                Description = "清除所有输入历史",
                Group = "输入",
                Execute = () => AnsiConsole.MarkupLine("[green]已清空历史[/]")
            },
            new CommandItem
            {
                Id = "show_status",
                Name = "显示状态",
                Description = "显示当前 Agent 状态信息",
                Group = "信息",
                Execute = () => AnsiConsole.MarkupLine("[blue]状态信息显示[/]")
            },
            new CommandItem
            {
                Id = "help",
                Name = "帮助",
                Description = "显示快捷键和用法说明",
                Group = "信息",
                Execute = () => AnsiConsole.MarkupLine("[cyan]帮助信息显示[/]")
            },
            new CommandItem
            {
                Id = "quit",
                Name = "退出",
                Description = "退出应用程序",
                Group = "系统",
                Execute = () => AnsiConsole.MarkupLine("[red]退出应用[/]")
            }
        };
    }
}