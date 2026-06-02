namespace Seeing.Agent.WebUI.Models;

/// <summary>
/// Agent Loop 分组视图模型 - 用于按 Loop 分组渲染消息
/// </summary>
public class LoopGroupViewModel
{
    /// <summary>
    /// Loop ID
    /// </summary>
    public string LoopId { get; set; } = string.Empty;

    /// <summary>
    /// Loop 索引（第几个 Loop，从 1 开始）
    /// </summary>
    public int LoopIndex { get; set; }

    /// <summary>
    /// Loop 内的消息列表
    /// </summary>
    public List<MessageViewModel> Messages { get; set; } = new();

    /// <summary>
    /// 用户消息（Loop 的触发消息）
    /// </summary>
    public MessageViewModel? UserMessage => Messages.FirstOrDefault(m => m.Role == "user");

    /// <summary>
    /// 助手消息（Loop 的响应消息）
    /// </summary>
    public MessageViewModel? AssistantMessage => Messages.FirstOrDefault(m => m.Role == "assistant");

    /// <summary>
    /// 是否已完成
    /// </summary>
    public bool IsComplete { get; set; }

    /// <summary>
    /// 是否正在执行
    /// </summary>
    public bool IsExecuting => !IsComplete;

    /// <summary>
    /// 总步数（LLM 调用次数）
    /// </summary>
    public int TotalSteps { get; set; }

    /// <summary>
    /// 执行耗时
    /// </summary>
    public TimeSpan? Duration { get; set; }

    /// <summary>
    /// 是否成功
    /// </summary>
    public bool Success { get; set; } = true;

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
    /// Token 使用统计
    /// </summary>
    public TokenUsageViewModel? TokenUsage { get; set; }

    /// <summary>
    /// 是否有思考内容
    /// </summary>
    public bool HasReasoning => Messages.Any(m => !string.IsNullOrEmpty(m.Reasoning));

    /// <summary>
    /// 是否有工具调用
    /// </summary>
    public bool HasToolCalls => Messages.Any(m => m.ToolCalls.Count > 0);

    /// <summary>
    /// 工具调用总数
    /// </summary>
    public int TotalToolCalls => Messages.Sum(m => m.ToolCalls.Count);

    /// <summary>
    /// 获取格式化的耗时显示
    /// </summary>
    public string GetFormattedDuration()
    {
        if (!Duration.HasValue)
            return string.Empty;

        var d = Duration.Value;
        if (d.TotalMinutes >= 1)
            return $"{(int)d.TotalMinutes}m {d.Seconds}s";
        if (d.TotalSeconds >= 1)
            return $"{(int)d.TotalSeconds}.{d.Milliseconds / 100}s";
        return $"{d.Milliseconds}ms";
    }

    /// <summary>
    /// 获取状态图标
    /// </summary>
    public string GetStatusIcon()
    {
        if (IsExecuting)
            return "loading";
        return Success ? "check-circle" : "close-circle";
    }

    /// <summary>
    /// 获取状态颜色
    /// </summary>
    public string GetStatusColor()
    {
        if (IsExecuting)
            return "var(--color-processing)";
        return Success ? "var(--color-success)" : "var(--color-error)";
    }
}

/// <summary>
/// Token 使用统计视图模型
/// </summary>
public class TokenUsageViewModel
{
    /// <summary>
    /// 输入 Token 数
    /// </summary>
    public int InputTokens { get; set; }

    /// <summary>
    /// 输出 Token 数
    /// </summary>
    public int OutputTokens { get; set; }

    /// <summary>
    /// 总 Token 数
    /// </summary>
    public int TotalTokens => InputTokens + OutputTokens;

    /// <summary>
    /// 格式化显示
    /// </summary>
    public string GetFormatted()
    {
        return $"{TotalTokens:N0} tokens (in: {InputTokens:N0}, out: {OutputTokens:N0})";
    }
}
