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

    /// <summary>
    /// 切换展开状态
    /// </summary>
    public void ToggleExpand()
    {
        IsExpanded = !IsExpanded;
    }
}