namespace Seeing.Agent.WebUI.Models;

/// <summary>
/// Todo 项状态
/// </summary>
public enum TodoStatusViewModel
{
    /// <summary>待处理</summary>
    Pending,
    /// <summary>进行中</summary>
    InProgress,
    /// <summary>已完成</summary>
    Completed,
    /// <summary>已取消</summary>
    Cancelled
}

/// <summary>
/// Todo 项优先级
/// </summary>
public enum TodoPriorityViewModel
{
    /// <summary>低优先级</summary>
    Low,
    /// <summary>中等优先级</summary>
    Medium,
    /// <summary>高优先级</summary>
    High
}

/// <summary>
/// Todo 项视图模型
/// </summary>
public class TodoItemViewModel
{
    /// <summary>任务唯一标识</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString()[..8];

    /// <summary>任务内容描述</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>任务状态</summary>
    public TodoStatusViewModel Status { get; set; } = TodoStatusViewModel.Pending;

    /// <summary>任务优先级</summary>
    public TodoPriorityViewModel Priority { get; set; } = TodoPriorityViewModel.Medium;

    #region UI 计算属性

    /// <summary>状态图标</summary>
    public string StatusIcon => Status switch
    {
        TodoStatusViewModel.Pending => "⏳",
        TodoStatusViewModel.InProgress => "🔄",
        TodoStatusViewModel.Completed => "✅",
        TodoStatusViewModel.Cancelled => "❌",
        _ => "⏳"
    };

    /// <summary>状态颜色（CSS 变量）</summary>
    public string StatusColor => Status switch
    {
        TodoStatusViewModel.Pending => "var(--color-text-secondary)",
        TodoStatusViewModel.InProgress => "var(--color-primary)",
        TodoStatusViewModel.Completed => "var(--color-success)",
        TodoStatusViewModel.Cancelled => "var(--color-error)",
        _ => "var(--color-text-secondary)"
    };

    /// <summary>状态标签颜色（AntDesign Tag）</summary>
    public string StatusTagColor => Status switch
    {
        TodoStatusViewModel.Pending => "default",
        TodoStatusViewModel.InProgress => "processing",
        TodoStatusViewModel.Completed => "success",
        TodoStatusViewModel.Cancelled => "error",
        _ => "default"
    };

    /// <summary>状态文本</summary>
    public string StatusText => Status switch
    {
        TodoStatusViewModel.Pending => "待处理",
        TodoStatusViewModel.InProgress => "进行中",
        TodoStatusViewModel.Completed => "已完成",
        TodoStatusViewModel.Cancelled => "已取消",
        _ => "未知"
    };

    /// <summary>优先级标签颜色</summary>
    public string PriorityTagColor => Priority switch
    {
        TodoPriorityViewModel.Low => "default",
        TodoPriorityViewModel.Medium => "warning",
        TodoPriorityViewModel.High => "error",
        _ => "default"
    };

    /// <summary>优先级文本</summary>
    public string PriorityText => Priority switch
    {
        TodoPriorityViewModel.Low => "低",
        TodoPriorityViewModel.Medium => "中",
        TodoPriorityViewModel.High => "高",
        _ => "中"
    };

    /// <summary>是否为当前任务</summary>
    public bool IsCurrentTask => Status == TodoStatusViewModel.InProgress;

    #endregion
}

/// <summary>
/// Todo 列表视图模型
/// </summary>
public class TodoListViewModel
{
    /// <summary>会话 ID</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Todo 项列表</summary>
    public List<TodoItemViewModel> Items { get; set; } = new();

    /// <summary>最后更新时间</summary>
    public DateTime? LastUpdated { get; set; }

    #region 统计属性

    /// <summary>总任务数</summary>
    public int TotalCount => Items.Count;

    /// <summary>已完成数</summary>
    public int CompletedCount => Items.Count(i => i.Status == TodoStatusViewModel.Completed);

    /// <summary>进行中数</summary>
    public int InProgressCount => Items.Count(i => i.Status == TodoStatusViewModel.InProgress);

    /// <summary>待处理数</summary>
    public int PendingCount => Items.Count(i => i.Status == TodoStatusViewModel.Pending);

    /// <summary>已取消数</summary>
    public int CancelledCount => Items.Count(i => i.Status == TodoStatusViewModel.Cancelled);

    /// <summary>进度百分比 (0-100)</summary>
    public double ProgressPercent => TotalCount > 0 ? Math.Round(CompletedCount * 100.0 / TotalCount, 1) : 0;

    /// <summary>进度条颜色</summary>
    public string ProgressColor => ProgressPercent switch
    {
        < 30 => "var(--color-error)",
        < 70 => "var(--color-warning)",
        < 100 => "var(--color-primary)",
        _ => "var(--color-success)"
    };

    /// <summary>当前任务</summary>
    public TodoItemViewModel? CurrentTask => Items.FirstOrDefault(i => i.Status == TodoStatusViewModel.InProgress);

    /// <summary>是否有任务</summary>
    public bool HasTasks => Items.Count > 0;

    /// <summary>是否全部完成</summary>
    public bool IsAllCompleted => TotalCount > 0 && CompletedCount == TotalCount;

    #endregion

    #region 工厂方法

    /// <summary>
    /// 从 JSON 数据创建 TodoListViewModel
    /// </summary>
    public static TodoListViewModel FromJson(string sessionId, string json)
    {
        var result = new TodoListViewModel { SessionId = sessionId };

        if (string.IsNullOrWhiteSpace(json))
            return result;

        try
        {
            var options = new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            // 尝试解析为数组
            using var doc = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonDocument>(json, options);
            if (doc?.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    var item = ParseTodoItem(element);
                    if (item != null)
                    {
                        result.Items.Add(item);
                    }
                }
            }
        }
        catch
        {
            // 解析失败返回空列表
        }

        return result;
    }

    /// <summary>
    /// 解析单个 Todo 项
    /// </summary>
    private static TodoItemViewModel? ParseTodoItem(System.Text.Json.JsonElement element)
    {
        try
        {
            string content = "";
            TodoStatusViewModel status = TodoStatusViewModel.Pending;
            TodoPriorityViewModel priority = TodoPriorityViewModel.Medium;

            // 解析 Content
            if (element.TryGetProperty("Content", out var contentProp))
            {
                content = contentProp.GetString() ?? "";
            }

            // 解析 Status（支持整数和字符串）
            if (element.TryGetProperty("Status", out var statusProp))
            {
                status = ParseStatusFromJson(statusProp);
            }

            // 解析 Priority（支持整数和字符串）
            if (element.TryGetProperty("Priority", out var priorityProp))
            {
                priority = ParsePriorityFromJson(priorityProp);
            }

            return new TodoItemViewModel
            {
                Id = Guid.NewGuid().ToString()[..8],
                Content = content,
                Status = status,
                Priority = priority
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 从 JSON 元素解析状态（支持整数和字符串）
    /// </summary>
    private static TodoStatusViewModel ParseStatusFromJson(System.Text.Json.JsonElement element)
    {
        if (element.ValueKind == System.Text.Json.JsonValueKind.Number)
        {
            return (TodoStatusViewModel)element.GetInt32();
        }

        if (element.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            return ParseStatus(element.GetString());
        }

        return TodoStatusViewModel.Pending;
    }

    /// <summary>
    /// 从 JSON 元素解析优先级（支持整数和字符串）
    /// </summary>
    private static TodoPriorityViewModel ParsePriorityFromJson(System.Text.Json.JsonElement element)
    {
        if (element.ValueKind == System.Text.Json.JsonValueKind.Number)
        {
            return (TodoPriorityViewModel)element.GetInt32();
        }

        if (element.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            return ParsePriority(element.GetString());
        }

        return TodoPriorityViewModel.Medium;
    }

    private static TodoStatusViewModel ParseStatus(string? status)
    {
        return status?.ToLowerInvariant() switch
        {
            "pending" => TodoStatusViewModel.Pending,
            "in_progress" => TodoStatusViewModel.InProgress,
            "completed" => TodoStatusViewModel.Completed,
            "cancelled" => TodoStatusViewModel.Cancelled,
            _ => TodoStatusViewModel.Pending
        };
    }

    private static TodoPriorityViewModel ParsePriority(string? priority)
    {
        return priority?.ToLowerInvariant() switch
        {
            "low" => TodoPriorityViewModel.Low,
            "medium" => TodoPriorityViewModel.Medium,
            "high" => TodoPriorityViewModel.High,
            _ => TodoPriorityViewModel.Medium
        };
    }

    #endregion
}
