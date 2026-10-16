using Seeing.Agent.Abstractions.Todo;
using Spectre.Console;
using Spectre.Console.Rendering;
using System.Text.Json;

namespace Seeing.Agent.Tui.Rendering;

/// <summary>
/// <c>todowrite</c> 工具输出（结构化 JSON）→ 可读任务列表卡片（纯函数）。
/// <para>
/// 工具 <c>Output</c> 是给模型的 <c>\uXXXX</c> 转义 + 数字枚举 JSON，直显不可读；
/// 本组件解析为「状态图标 + 内容 + 优先级」逐项列表，视觉对齐 WebUI <c>TodoCard</c>。
/// </para>
/// <para>
/// 数据来源：优先解析 <c>output</c>（落盘结果，裸数组）；为空（运行中）退回 <c>arguments</c>
/// （模型意图，形如 <c>{"todos":[...]}</c>）。解析失败返回 <c>false</c>，调用方退回原样展示。
/// </para>
/// </summary>
internal static class TodoCardView
{
    /// <summary>尝试把 todowrite 的 output/arguments 渲染成任务列表卡片；无法解析时返回 false。</summary>
    public static bool TryBuild(string? output, string? arguments, int width, out IRenderable card)
    {
        card = null!;

        if (!TryExtractItems(output, out var items) && !TryExtractItems(arguments, out items))
            return false;

        var rows = new List<IRenderable>();
        if (items.Count == 0)
        {
            rows.Add(new Markup("[grey]（无待办）[/]"));
        }
        else
        {
            // Panel 边框 + 左右 padding 各占 1 格，内容可用宽 = width - 4。
            var contentWidth = Math.Max(0, width - 4);
            foreach (var item in items)
            {
                var box = StatusBox(item.Status);
                var color = StatusColor(item.Status);
                var isDone = item.Status == TodoStatus.Completed;

                // 行首：状态标识显示宽 + 空格；再扣除优先级标注，剩余预算给内容。
                var priority = PrioritySuffix(item.Priority, isDone);
                var prefixWidth = DisplayText.Width(box) + 1;
                var budget = Math.Max(0, contentWidth - prefixWidth - DisplayText.Width(priority));
                var content = FitToWidth(item.Content, budget);

                // 已完成项整行弱化（图标绿、正文 grey），一眼区分「已完成 / 待办」。
                var textColor = isDone ? "grey" : "default";
                rows.Add(new Markup(
                    $"[{color}]{Markup.Escape(box)}[/] [{textColor}]{Markup.Escape(content)}[/]{priority}"));
            }
        }

        var done = items.Count(i => i.Status == TodoStatus.Completed);
        card = new Panel(new Rows(rows))
            .Header($" 任务列表 {done}/{items.Count} ", Justify.Left)
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Grey);
        return true;
    }

    /// <summary>解析 JSON：既接受裸数组（output），也接受 <c>{"todos":[...]}</c>（arguments）。</summary>
    private static bool TryExtractItems(string? json, out List<TodoEntry> items)
    {
        items = [];

        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            JsonElement array;
            if (root.ValueKind == JsonValueKind.Array)
            {
                array = root;
            }
            else if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("todos", out var todos)
                && todos.ValueKind == JsonValueKind.Array)
            {
                array = todos;
            }
            else
            {
                return false;
            }

            foreach (var element in array.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                    continue;

                var content = element.TryGetProperty("content", out var contentProp)
                    ? contentProp.GetString() ?? string.Empty
                    : string.Empty;
                var status = element.TryGetProperty("status", out var statusProp)
                    ? ParseStatus(statusProp)
                    : TodoStatus.Pending;
                var priority = element.TryGetProperty("priority", out var priorityProp)
                    ? ParsePriority(priorityProp)
                    : TodoPriority.Medium;

                items.Add(new TodoEntry(content, status, priority));
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>状态支持数字枚举（output）与字符串（arguments）。</summary>
    private static TodoStatus ParseStatus(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number => (TodoStatus)element.GetInt32(),
        JsonValueKind.String => element.GetString()?.ToLowerInvariant() switch
        {
            "in_progress" => TodoStatus.InProgress,
            "completed" => TodoStatus.Completed,
            "cancelled" => TodoStatus.Cancelled,
            "paused" => TodoStatus.Paused,
            _ => TodoStatus.Pending,
        },
        _ => TodoStatus.Pending,
    };

    /// <summary>优先级支持数字枚举（output）与字符串（arguments）。</summary>
    private static TodoPriority ParsePriority(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number => (TodoPriority)element.GetInt32(),
        JsonValueKind.String => element.GetString()?.ToLowerInvariant() switch
        {
            "low" => TodoPriority.Low,
            "high" => TodoPriority.High,
            _ => TodoPriority.Medium,
        },
        _ => TodoPriority.Medium,
    };

    /// <summary>仅非 medium 时在行尾标注优先级；已完成项弱化标注。</summary>
    private static string PrioritySuffix(TodoPriority priority, bool done)
    {
        if (priority == TodoPriority.Medium)
            return string.Empty;

        var text = priority == TodoPriority.High ? "(high)" : "(low)";
        if (done)
            return $" [grey]{text}[/]";

        return priority == TodoPriority.High ? $" [red]{text}[/]" : $" [grey]{text}[/]";
    }

    /// <summary>
    /// 状态复选框，统一 4 显示格宽以对齐：待办 <c>[  ]</c>、进行中 <c>[~ ]</c>、
    /// 已完成 <c>[√]</c>（√ 占 2 格）、取消 <c>[- ]</c>、暂停 <c>[. ]</c>。
    /// </summary>
    private static string StatusBox(TodoStatus status)
    {
        var inner = status switch
        {
            TodoStatus.InProgress => "~",
            TodoStatus.Completed => TuiGlyphs.Success,
            TodoStatus.Cancelled => "-",
            TodoStatus.Paused => ".",
            _ => string.Empty,
        };

        var pad = Math.Max(0, 2 - DisplayText.Width(inner));
        return $"[{inner}{new string(' ', pad)}]";
    }

    private static string StatusColor(TodoStatus status) => status switch
    {
        TodoStatus.InProgress => "yellow",
        TodoStatus.Completed => "green",
        _ => "grey",
    };

    private static string FitToWidth(string value, int maxWidth) => TuiRenderer.FitToWidth(value, maxWidth);

    private readonly record struct TodoEntry(string Content, TodoStatus Status, TodoPriority Priority);
}
