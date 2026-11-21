using Seeing.Agent.Abstractions.Background;

namespace Seeing.Agent.Tui.Services;

public enum TuiBlockKind
{
    User,
    Assistant,
    Tool,
    Error,
    System,
    Compaction,
}

public enum TuiToolStatus
{
    Pending,
    Running,
    Success,
    Failed,
    Rejected,
    Cancelled,
}

public sealed record TuiTaskStep(string ToolName, string Summary, TuiToolStatus Status);

public sealed record TuiTodo(string Content, string Status, string? ActiveForm);

/// <summary>排队中的入站 user 消息（已入站、尚未被消费循环消费）：输入区上方独立展示，消费后提升为正式 User 块。</summary>
public sealed record TuiPendingMessage
{
    public required string Id { get; init; }
    public required string Text { get; init; }
}

/// <summary>上下文用量（当前占用 / 上限）。上限未知（0 或未配置）时为 null，状态栏只显示已用量。</summary>
public sealed record TuiBudget(long CurrentTokens, long? MaxTokens);

public sealed class TuiToolState
{
    public required string CallId { get; init; }
    public required string Name { get; init; }
    public TuiToolStatus Status { get; set; }
    public string? Arguments { get; set; }
    public string? Output { get; set; }
    public string? Error { get; set; }
    public string? Title { get; set; }
    public string? TaskId { get; set; }
    public string? TaskAgent { get; set; }
    public string? TaskDescription { get; set; }
    /// <summary>本调用转交的后台活动 id（如 bash background=true）。</summary>
    public string? BackgroundActivityId { get; set; }

    /// <summary>本调用是否转成了后台（等待者被放行）；前台正常完成为 false。</summary>
    public bool BackgroundDetached { get; set; }

    /// <summary>关联的活动 id（来自 <c>activity.*</c> 事件；前台命令也是一个活动）。</summary>
    public string? ActivityId { get; set; }

    /// <summary>活动状态（仅实时事件可得，不落盘）。</summary>
    public BackgroundActivityStatus? ActivityStatus { get; set; }

    /// <summary>
    /// 活动实时输出尾部（<c>activity.progress</c> 携带；有界、非权威）。
    /// 渲染时仅在运行中优先于 <see cref="Output"/> 展示。
    /// </summary>
    public string? ActivityOutput { get; set; }

    public List<TuiTaskStep> Steps { get; } = [];
    public bool IsExpanded { get; set; }
}

public sealed class TuiBlock
{
    public required string Key { get; init; }
    public required TuiBlockKind Kind { get; init; }
    public string? LoopId { get; init; }
    public int Step { get; init; }
    public string Text { get; set; } = string.Empty;
    public string Reasoning { get; set; } = string.Empty;
    public string? Title { get; set; }
    public bool IsStreaming { get; set; }
    public bool IsCancelled { get; set; }
    public bool IsTerminal { get; set; }
    public TuiToolState? Tool { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}
