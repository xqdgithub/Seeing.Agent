using Seeing.Agent.Tui.Services;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Seeing.Agent.Tui.Rendering;

/// <summary>
/// 状态栏（纯渲染辅助）：Agent / 模型 / 执行态 / 队列 / 待批 / Todo / 预算。
/// 统计信息全部经方法参数传入，不依赖其它服务。
/// </summary>
public static class StatusBarView
{
    public static IRenderable Render(
        TuiViewState state,
        int width,
        int pendingApprovals = 0,
        int backgroundExecutions = 0,
        string? hint = null)
    {
        try
        {
            var parts = new List<string>
            {
                $"[grey]{Escape(state.AgentId, "-")}[/]",
            };

            if (!string.IsNullOrEmpty(state.ModelId))
                parts.Add($"[blue]{Markup.Escape(state.ModelId)}[/]");

            if (!string.IsNullOrEmpty(state.ThinkingEffort))
                parts.Add($"[grey]think:{Markup.Escape(state.ThinkingEffort)}[/]");

            parts.Add(state.IsExecuting ? "[yellow]运行中[/]" : "[green]空闲[/]");

            if (state.QueueLength > 0)
                parts.Add($"[cyan]队列 {state.QueueLength}[/]");

            if (pendingApprovals > 0)
                parts.Add($"[red]待批 {pendingApprovals}[/]");

            if (backgroundExecutions > 0)
                parts.Add($"[grey]{backgroundExecutions} 后台执行[/]");

            if (!string.IsNullOrEmpty(hint))
                parts.Add($"[yellow]{Markup.Escape(hint)}[/]");

            var todo = TodoSummary(state.Todos);
            if (todo is not null)
                parts.Add($"[grey]{todo}[/]");

            if (state.Budget is not null)
                parts.Add(BudgetSummary(state.Budget));

            var line = string.Join(" [grey]·[/] ", parts);
            return new Markup($"[grey11] {line} [/]");
        }
        catch
        {
            return new Text(string.Empty);
        }
    }

    private static string? TodoSummary(IReadOnlyList<TuiTodo> todos)
    {
        if (todos is null || todos.Count == 0)
            return null;

        var done = todos.Count(t => string.Equals(t.Status, "completed", StringComparison.OrdinalIgnoreCase));
        return $"Todo {done}/{todos.Count}";
    }

    private static string BudgetSummary(TuiBudget budget)
    {
        var text = $"tokens {budget.InputTokens}/{budget.OutputTokens}";
        if (budget.Limit is > 0)
        {
            var used = budget.InputTokens + budget.OutputTokens;
            var percent = Math.Min(999, used * 100 / budget.Limit.Value);
            text += $" ({percent}%/{budget.Limit})";
        }

        return $"[grey]{text}[/]";
    }

    private static string Escape(string? value, string fallback)
        => Markup.Escape(string.IsNullOrEmpty(value) ? fallback : value);
}
