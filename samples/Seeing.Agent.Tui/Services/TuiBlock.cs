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
