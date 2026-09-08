namespace Seeing.Agent.Abstractions.Events;

/// <summary>
/// Schema 快照事件 — 通知 UI 当前执行可用的工具与区块 schema
/// </summary>
public sealed record SchemaSnapshotEvent : IMessageEvent
{
    /// <inheritdoc />
    public string Type => MessageEventType.SchemaSnapshot;

    /// <inheritdoc />
    public required string SessionId { get; init; }

    /// <inheritdoc />
    public string? LoopId { get; init; }

    /// <inheritdoc />
    public DateTime Timestamp { get; init; } = DateTime.Now;

    /// <summary>执行 ID</summary>
    public required string ExecutionId { get; init; }

    /// <summary>可用工具 ID 列表</summary>
    public IReadOnlyList<string> ToolIds { get; init; } = Array.Empty<string>();

    /// <summary>可用区块 ID 列表</summary>
    public IReadOnlyList<string> SectionIds { get; init; } = Array.Empty<string>();
}
