using System.Text;

using Seeing.Agent.Abstractions.Todo;
namespace Seeing.Agent.Core.Todo;

public static class TodoListExtensions
{
    public static bool IsEmpty(this TodoList list) =>
        list.Items.Count == 0;

    public static bool HasIncompletePendingOrInProgress(this TodoList list) =>
        list.Items.Any(t => t.Status is TodoStatus.Pending or TodoStatus.InProgress);

    public static bool HasPaused(this TodoList list) =>
        list.Items.Any(t => t.Status == TodoStatus.Paused);

    public static string FormatBrief(this TodoList list)
    {
        if (list.Items.Count == 0)
            return "（无任务）";

        var sb = new StringBuilder();
        foreach (var item in list.Items.OrderBy(t => t.Status))
        {
            var statusMark = item.Status switch
            {
                TodoStatus.Completed => "[✔]",
                TodoStatus.InProgress => "[▶]",
                TodoStatus.Pending => "[ ]",
                TodoStatus.Cancelled => "[✗]",
                TodoStatus.Paused => "[⏸]",
                _ => "[ ]"
            };
            sb.AppendLine($"  {statusMark} {item.Content}");
        }
        return sb.ToString();
    }
}
