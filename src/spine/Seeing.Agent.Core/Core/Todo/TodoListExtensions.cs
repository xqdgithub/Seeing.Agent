using System.Text;

using Seeing.Agent.Abstractions.Todo;
namespace Seeing.Agent.Core.Todo;

/// <summary>
/// TodoList 查询与格式化的扩展方法集合。
/// </summary>
public static class TodoListExtensions
{
    /// <summary>
    /// 判断任务列表是否为空。
    /// </summary>
    public static bool IsEmpty(this TodoList list) =>
        list.Items.Count == 0;

    /// <summary>
    /// 判断是否存在待办或进行中的未完成任务。
    /// </summary>
    public static bool HasIncompletePendingOrInProgress(this TodoList list) =>
        list.Items.Any(t => t.Status is TodoStatus.Pending or TodoStatus.InProgress);

    /// <summary>
    /// 判断是否存在暂停状态的任务。
    /// </summary>
    public static bool HasPaused(this TodoList list) =>
        list.Items.Any(t => t.Status == TodoStatus.Paused);

    /// <summary>
    /// 将任务列表按状态排序渲染为带状态标记的简要文本。
    /// </summary>
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
