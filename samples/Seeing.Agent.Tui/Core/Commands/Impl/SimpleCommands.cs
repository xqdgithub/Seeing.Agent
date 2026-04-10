using Seeing.Agent.Commands;

namespace Seeing.Agent.Tui.Core.Commands.Impl;

/// <summary>
/// 清空命令
/// </summary>
public class ClearCommand : ICommand
{
    private readonly Core.TuiState _state;

    public ClearCommand(Core.TuiState state) => _state = state;

    public CommandMetadata Metadata => new()
    {
        Name = "clear",
        Description = "清空对话历史",
        Usage = "/clear",
        Category = CommandCategory.Basic,
        SortOrder = 10
    };

    public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        _state.ClearMessages();
        return Task.FromResult(CommandResult.Ok("[grey]已清空对话历史[/]", true));
    }
}

/// <summary>
/// 退出命令
/// </summary>
public class ExitCommand : ICommand
{
    public CommandMetadata Metadata => new()
    {
        Name = "exit",
        Aliases = new[] { "quit" },
        Description = "退出程序",
        Usage = "/exit 或 /quit",
        Category = CommandCategory.Basic,
        SortOrder = 5
    };

    public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        return Task.FromResult(CommandResult.Exit("再见！"));
    }
}

/// <summary>
/// 取消命令
/// </summary>
public class CancelCommand : ICommand
{
    private readonly Core.TuiState _state;

    public CancelCommand(Core.TuiState state) => _state = state;

    public CommandMetadata Metadata => new()
    {
        Name = "cancel",
        Description = "取消当前任务",
        Usage = "/cancel",
        Category = CommandCategory.Basic,
        SortOrder = 15
    };

    public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        if (_state.IsProcessing)
        {
            _state.CancelCurrentTask();
            return Task.FromResult(CommandResult.Ok("[yellow]已取消当前任务[/]"));
        }
        return Task.FromResult(CommandResult.Ok("[grey]没有正在执行的任务[/]"));
    }
}

/// <summary>
/// 多行模式切换命令
/// </summary>
public class MultilineCommand : ICommand
{
    private readonly Core.TuiState _state;

    public MultilineCommand(Core.TuiState state) => _state = state;

    public CommandMetadata Metadata => new()
    {
        Name = "multiline",
        Aliases = new[] { "ml" },
        Description = "切换多行/单行输入模式",
        Usage = "/multiline 或 /ml",
        Category = CommandCategory.Basic,
        SortOrder = 20
    };

    public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        _state.IsMultilineMode = !_state.IsMultilineMode;
        var message = _state.IsMultilineMode
            ? "[yellow]已切换到多行输入模式[/] [dim](空行发送)[/]"
            : "[green]已切换到单行输入模式[/] [dim](Enter发送)[/]";
        return Task.FromResult(CommandResult.Ok(message));
    }
}