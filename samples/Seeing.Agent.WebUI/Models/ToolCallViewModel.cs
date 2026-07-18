using Seeing.Session.Core;

namespace Seeing.Agent.WebUI.Models;

/// <summary>
/// 工具调用视图模型，用于 UI 显示
/// </summary>
public class ToolCallViewModel
{
    /// <summary>
    /// 工具调用唯一标识
    /// </summary>
    public string Id { get; set; } = "";

    /// <summary>
    /// 工具名称
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// 工具描述
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// 调用状态 (Pending/Running/Success/Failed)
    /// </summary>
    public string Status { get; set; } = "Pending";

    /// <summary>
    /// 工具参数（JSON 格式）
    /// </summary>
    public string? Parameters { get; set; }

    /// <summary>
    /// 执行结果
    /// </summary>
    public string? Result { get; set; }

    /// <summary>
    /// 错误信息
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// 开始时间
    /// </summary>
    public DateTime? StartTime { get; set; }

    /// <summary>
    /// 结束时间
    /// </summary>
    public DateTime? EndTime { get; set; }

    /// <summary>
    /// 执行耗时（毫秒）
    /// </summary>
    public double? DurationMs { get; set; }

    /// <summary>
    /// 关联的消息 ID
    /// </summary>
    public string? MessageId { get; set; }

    /// <summary>
    /// 是否展开详情（UI 状态）
    /// </summary>
    public bool IsExpanded { get; set; } = false;

    /// <summary>
    /// 工具调用在内容中的位置（UI 层自行管理，核心层不传递）
    /// </summary>
    public int? ContentPosition { get; set; }

    /// <summary>
    /// Todo 列表（仅 todowrite 工具有效）
    /// </summary>
    public TodoListViewModel? TodoList { get; set; }

    /// <summary>
    /// 是否为 Todo 工具
    /// </summary>
    public bool IsTodoTool => Name?.ToLowerInvariant() == "todowrite";

    /// <summary>
    /// 子任务 ID（≡ Child Session Id）
    /// </summary>
    public string? TaskId { get; set; }

    /// <summary>
    /// 子任务 Agent
    /// </summary>
    public string? TaskAgent { get; set; }

    /// <summary>
    /// 子任务描述
    /// </summary>
    public string? TaskDescription { get; set; }

    /// <summary>
    /// 是否后台任务
    /// </summary>
    public bool TaskBackground { get; set; }

    /// <summary>
    /// 子任务步骤
    /// </summary>
    public List<SessionTaskStep> TaskSteps { get; set; } = new();

    /// <summary>
    /// 是否为 Task 子代理工具卡片
    /// </summary>
    public bool IsTaskTool =>
        string.Equals(Name, "task", StringComparison.OrdinalIgnoreCase)
        || !string.IsNullOrEmpty(TaskId)
        || (!string.IsNullOrEmpty(Result) &&
            Result.Contains("task_id:", StringComparison.OrdinalIgnoreCase))
        || (!string.IsNullOrEmpty(Parameters) &&
            Parameters.Contains("\"subagent_type\"", StringComparison.OrdinalIgnoreCase));

    // ========== 工厂方法 ==========

    /// <summary>
    /// 从 SessionToolCall 创建 ToolCallViewModel
    /// </summary>
    /// <param name="tc">会话工具调用数据</param>
    /// <param name="sessionId">会话 ID（用于 todowrite 工具解析 Todo 列表）</param>
    /// <returns>工具调用视图模型</returns>
    public static ToolCallViewModel FromSessionToolCall(SessionToolCall tc, string sessionId)
    {
        var vm = new ToolCallViewModel
        {
            Id = tc.Id,
            Name = tc.Name,
            Parameters = tc.Arguments,
            Result = tc.Result,
            Status = tc.Status,
            Error = tc.Error,
            TaskId = tc.TaskId,
            TaskAgent = tc.TaskAgent,
            TaskDescription = tc.TaskDescription,
            TaskBackground = tc.TaskBackground,
            TaskSteps = tc.TaskSteps?.ToList() ?? new List<SessionTaskStep>()
        };

        // 持久化/刷新后可能丢失 Task* 字段：从参数与结果回填
        RecoverTaskFields(vm);

        // todowrite 工具特殊处理：解析 Todo 列表
        if (vm.IsTodoTool &&
            string.Equals(tc.Status, "success", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrEmpty(tc.Result))
        {
            try
            {
                vm.TodoList = TodoListViewModel.FromJson(sessionId, tc.Result);
            }
            catch
            {
                // 解析失败，忽略
            }
        }

        return vm;
    }

    private static void RecoverTaskFields(ToolCallViewModel vm)
    {
        var looksLikeTask =
            string.Equals(vm.Name, "task", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(vm.TaskId)
            || (!string.IsNullOrEmpty(vm.Result) &&
                vm.Result.Contains("task_id:", StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrEmpty(vm.Parameters) &&
                vm.Parameters.Contains("\"subagent_type\"", StringComparison.OrdinalIgnoreCase));

        if (!looksLikeTask)
            return;

        if (string.IsNullOrEmpty(vm.Name))
            vm.Name = "task";

        if (string.IsNullOrEmpty(vm.TaskId) && !string.IsNullOrEmpty(vm.Result))
        {
            const string prefix = "task_id:";
            var idx = vm.Result.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var start = idx + prefix.Length;
                while (start < vm.Result.Length && char.IsWhiteSpace(vm.Result[start]))
                    start++;
                var end = start;
                while (end < vm.Result.Length && !char.IsWhiteSpace(vm.Result[end]))
                    end++;
                if (end > start)
                    vm.TaskId = vm.Result[start..end];
            }
        }

        if (string.IsNullOrWhiteSpace(vm.Parameters))
            return;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(vm.Parameters);
            var root = doc.RootElement;
            if (string.IsNullOrEmpty(vm.TaskDescription) &&
                root.TryGetProperty("description", out var desc) &&
                desc.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                vm.TaskDescription = desc.GetString();
            }

            if (string.IsNullOrEmpty(vm.TaskAgent) &&
                root.TryGetProperty("subagent_type", out var agent) &&
                agent.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                vm.TaskAgent = agent.GetString();
            }

            if (!vm.TaskBackground &&
                root.TryGetProperty("background", out var bg) &&
                bg.ValueKind == System.Text.Json.JsonValueKind.True)
            {
                vm.TaskBackground = true;
            }
        }
        catch
        {
            // ignore
        }
    }

    // ========== 状态计算属性 ==========

    /// <summary>
    /// 获取状态图标名称（AntDesign IconType）
    /// </summary>
    public string StatusIcon => Status?.ToLowerInvariant() switch
    {
        "pending" => "clock-circle",
        "running" => "loading",
        "success" => "check-circle",
        "failed" => "close-circle",
        "rejected" => "stop",
        _ => "question-circle"
    };

    /// <summary>
    /// 获取状态图标的 CSS 样式
    /// </summary>
    public string StatusIconStyle => Status?.ToLowerInvariant() switch
    {
        "pending" => "color: var(--color-warning); font-size: 14px;",
        "running" => "color: var(--color-primary); font-size: 14px; animation: spin 1s linear infinite;",
        "success" => "color: var(--color-success); font-size: 14px;",
        "failed" => "color: var(--color-error); font-size: 14px;",
        "rejected" => "color: var(--color-text-tertiary); font-size: 14px;",
        _ => "color: var(--color-text-tertiary); font-size: 14px;"
    };

    /// <summary>
    /// 获取状态标签颜色（AntDesign Tag Color）
    /// </summary>
    public string StatusTagColor => Status?.ToLowerInvariant() switch
    {
        "pending" => "warning",
        "running" => "processing",
        "success" => "success",
        "failed" => "error",
        "rejected" => "default",
        "cancelled" => "default",
        _ => "default"
    };

    /// <summary>
    /// 获取状态显示文本（中文）
    /// </summary>
    public string StatusText => Status?.ToLowerInvariant() switch
    {
        "pending" => "等待",
        "running" => "执行中",
        "success" => "完成",
        "failed" => "失败",
        "rejected" => "拒绝",
        "cancelled" => "已取消",
        _ => Status ?? "未知"
    };

    /// <summary>
    /// 是否为执行中状态
    /// </summary>
    public bool IsRunning => Status?.ToLowerInvariant() is "running" or "pending";

    // ========== 计算结果 ==========

    /// <summary>
    /// 计算执行耗时
    /// </summary>
    public void CalculateDuration()
    {
        if (StartTime.HasValue && EndTime.HasValue)
        {
            DurationMs = (EndTime.Value - StartTime.Value).TotalMilliseconds;
        }
    }

    /// <summary>
    /// 获取格式化的耗时显示
    /// </summary>
    public string GetFormattedDuration()
    {
        if (!DurationMs.HasValue) return "";

        if (DurationMs.Value < 1000)
            return $"{DurationMs.Value:F0}ms";
        else if (DurationMs.Value < 60000)
            return $"{DurationMs.Value / 1000:F2}s";
        else
            return $"{DurationMs.Value / 60000:F1}min";
    }
}
