namespace Seeing.Agent.Abstractions.Events;

/// <summary>压缩开始</summary>
public sealed record CompactionStartedEvent : IMessageEvent
{
    /// <inheritdoc />
    public string Type => CompactionEventTypes.Started;

    /// <inheritdoc />
    public required string SessionId { get; init; }

    /// <inheritdoc />
    public string? LoopId { get; init; }

    /// <inheritdoc />
    public DateTime Timestamp { get; init; } = DateTime.Now;

    /// <summary>压缩触发原因（manual/auto/api）</summary>
    public required string Reason { get; init; }
}

/// <summary>压缩增量进度</summary>
public sealed record CompactionDeltaEvent : IMessageEvent
{
    /// <inheritdoc />
    public string Type => CompactionEventTypes.Delta;

    /// <inheritdoc />
    public required string SessionId { get; init; }

    /// <inheritdoc />
    public string? LoopId { get; init; }

    /// <inheritdoc />
    public DateTime Timestamp { get; init; } = DateTime.Now;

    /// <summary>进度阶段（summarizing/trimming/…）</summary>
    public string Stage { get; init; } = string.Empty;
}

/// <summary>压缩完成</summary>
public sealed record CompactionCompletedEvent : IMessageEvent
{
    /// <inheritdoc />
    public string Type => CompactionEventTypes.Completed;

    /// <inheritdoc />
    public required string SessionId { get; init; }

    /// <inheritdoc />
    public string? LoopId { get; init; }

    /// <inheritdoc />
    public DateTime Timestamp { get; init; } = DateTime.Now;

    /// <summary>压缩前 token 数</summary>
    public int TokensBefore { get; init; }

    /// <summary>压缩后 token 数</summary>
    public int TokensAfter { get; init; }

    /// <summary>移除消息条数</summary>
    public int MessagesRemoved { get; init; }

    /// <summary>生成的摘要文本</summary>
    public string? Summary { get; init; }
}

/// <summary>压缩失败</summary>
public sealed record CompactionFailedEvent : IMessageEvent
{
    /// <inheritdoc />
    public string Type => CompactionEventTypes.Failed;

    /// <inheritdoc />
    public required string SessionId { get; init; }

    /// <inheritdoc />
    public string? LoopId { get; init; }

    /// <inheritdoc />
    public DateTime Timestamp { get; init; } = DateTime.Now;

    /// <summary>错误信息</summary>
    public string? ErrorMessage { get; init; }
}