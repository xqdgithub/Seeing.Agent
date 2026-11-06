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

    /// <summary>
    /// 本轮的 <c>tools[]</c> 是否与同一会话上一轮不同（前缀缓存会从变化点起失效）。
    /// <para>
    /// 已知合法变化：Agent / 场景切换、MCP 连接增减、跨日（环境分节）。该标志只陈述事实，
    /// 不包含"是否异常"的判断——用 <see cref="AddedToolIds"/> / <see cref="RemovedToolIds"/> 定位原因。
    /// </para>
    /// <para>首轮（无基线）恒为 false。</para>
    /// </summary>
    public bool SchemaChangedFromPreviousExecution { get; init; }

    /// <summary>相对上一轮新增的内联工具 id（顺序变化时为两者差异集，可能都为空）。</summary>
    public IReadOnlyList<string> AddedToolIds { get; init; } = Array.Empty<string>();

    /// <summary>相对上一轮消失的内联工具 id。</summary>
    public IReadOnlyList<string> RemovedToolIds { get; init; } = Array.Empty<string>();
}
