using Seeing.Agent.Abstractions.Tools;

namespace Seeing.Agent.Abstractions.Events;

/// <summary>
/// Schema 快照事件 — 通知 UI 当前执行可用的工具与区块 schema，并携带本轮暴露度量。
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

    /// <summary>本轮生效的暴露策略模式（反映实际行为：auto 升级后为 Tiered）。</summary>
    public ToolExposurePolicyMode ExposureMode { get; init; } = ToolExposurePolicyMode.Auto;

    /// <summary>本轮内联暴露的工具数。</summary>
    public int InlineToolCount { get; init; }

    /// <summary>本轮延迟（进目录）的工具数。</summary>
    public int DeferredToolCount { get; init; }

    /// <summary>本轮内联工具定义的估算 token。</summary>
    public int EstimatedDefinitionTokens { get; init; }

    /// <summary>本轮目录的估算 token（与定义口径分列）。</summary>
    public int CatalogTokens { get; init; }
}
