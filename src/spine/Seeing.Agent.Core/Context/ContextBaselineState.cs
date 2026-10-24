using System.Text.Json.Nodes;

namespace Seeing.Agent.Core.Context;

/// <summary>
/// 待处理的生命周期重置事件（位集合：可合并，不互相覆盖）。
/// </summary>
[Flags]
internal enum PendingBaselineReset
{
    /// <summary>无待处理事件。</summary>
    None = 0,

    /// <summary>压缩后：重新叙述（使 <c>Target=Message</c> 的初始渲染重新可见）。</summary>
    Compaction = 1,

    /// <summary>工作区变更（cwd 切换等）：整体重建基线。</summary>
    WorkspaceChanged = 2
}

/// <summary>
/// 会话级上下文基线状态（<see cref="Seeing.Session.Core.SessionMetadataKeys.ContextBaseline"/>）。
/// <para>只持久化规范化值与已渲染文本；源 Payload 每次读取重新取得，不落盘。</para>
/// </summary>
internal sealed class ContextBaselineState
{
    /// <summary>当前状态结构版本；版本不匹配时整体丢弃重建。</summary>
    internal const int CurrentStateVersion = 1;

    /// <summary>状态结构版本。</summary>
    public int StateVersion { get; set; } = CurrentStateVersion;

    /// <summary>待处理的生命周期重置位。</summary>
    public PendingBaselineReset Pending { get; set; } = PendingBaselineReset.None;

    /// <summary>按源键（<c>ContextSourceKey.Value</c>）索引的源状态。</summary>
    public Dictionary<string, ContextSourceState> Sources { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// 单个源的已叙述状态。
/// </summary>
internal sealed class ContextSourceState
{
    /// <summary>该源的值结构版本；变化时基线整体重建。</summary>
    public int ValueSchemaVersion { get; set; }

    /// <summary>已渲染基线文本（字节锁与重放的唯一来源）。</summary>
    public string BaselineText { get; set; } = string.Empty;

    /// <summary>
    /// 已渲染基线文本的 SHA-256（同值重渲染的字节比对；不是值的哈希，
    /// 值的规范化快照见 <see cref="BaselineValues"/>）。
    /// </summary>
    public string BaselineHash { get; set; } = string.Empty;

    /// <summary>基线值的规范化快照（不含 Payload）。</summary>
    public JsonObject? BaselineValues { get; set; }

    /// <summary>已向模型叙述过的最新值（不含 Payload）。</summary>
    public JsonObject? CurrentValues { get; set; }

    /// <summary><c>Target=Message</c> 时初始渲染所在消息 Id。</summary>
    public string? BaselineMessageId { get; set; }

    /// <summary>已叙述增量的快照账本（用于精确回退，避免重复叙述）。</summary>
    public List<DeltaLedgerEntry> DeltaLedger { get; set; } = new();
}

/// <summary>
/// 一条已叙述增量的快照。
/// </summary>
/// <param name="MessageId">承载该增量的消息 Id。</param>
/// <param name="Values">叙述完成后该源的值快照。</param>
internal sealed record DeltaLedgerEntry(string MessageId, JsonObject Values);
