namespace Seeing.Agent.Abstractions.Events;

/// <summary>
/// 工具检索事件 — <c>tool_search</c> 每次检索后发布，供 UI/诊断观测检索口径与命中集合。
/// <para>镜像 <see cref="SchemaSnapshotEvent"/> 结构；Type 采用字面量常量（MessageEventType 为开放常量类，不新增成员）。</para>
/// </summary>
public sealed record ToolSearchEvent : IMessageEvent
{
    /// <summary>本事件类型标识（字面量，不改动 MessageEventType）。</summary>
    public const string EventTypeName = "tool.search";

    /// <inheritdoc />
    public string Type => EventTypeName;

    /// <inheritdoc />
    public required string SessionId { get; init; }

    /// <inheritdoc />
    public string? LoopId { get; init; }

    /// <inheritdoc />
    public DateTime Timestamp { get; init; } = DateTime.Now;

    /// <summary>执行 ID</summary>
    public string? ExecutionId { get; init; }

    /// <summary>检索关键词（可空 = 概览全部）</summary>
    public string? Query { get; init; }

    /// <summary>命名空间限定（可空）</summary>
    public string? Namespace { get; init; }

    /// <summary>详情档位：name / summary / full</summary>
    public string Detail { get; init; } = "summary";

    /// <summary>分页起始偏移</summary>
    public int Offset { get; init; }

    /// <summary>命中总数</summary>
    public int Total { get; init; }

    /// <summary>本页返回数</summary>
    public int Returned { get; init; }

    /// <summary>剩余条数</summary>
    public int Remaining { get; init; }

    /// <summary>本页命中的工具 ID</summary>
    public IReadOnlyList<string> HitIds { get; init; } = Array.Empty<string>();
}
