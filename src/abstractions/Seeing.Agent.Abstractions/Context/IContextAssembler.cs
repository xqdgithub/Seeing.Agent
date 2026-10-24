using Seeing.Session.Core;

namespace Seeing.Agent.Abstractions.Context;

/// <summary>
/// 装配模式。
/// </summary>
public enum ContextAssemblyMode
{
    /// <summary>正常执行：处理生命周期事件、插入增量消息，并把状态写回会话。</summary>
    Persist,

    /// <summary>
    /// 旁路只读装配（Scheduler / 无 UI 宿主）：只计算提示词，不插入消息、不写状态。
    /// </summary>
    ReadOnly
}

/// <summary>
/// 一次装配的结果；纯数据，不含源对象。
/// </summary>
/// <param name="SystemBaseline">系统提示词末尾的基线文本（字节稳定）。</param>
/// <param name="HistoryBaseline">历史消息侧的基线文本（无则为 null）。</param>
/// <param name="DeltaText">本次要叙述的增量文本（无变化则为 null）。</param>
/// <param name="ChangedSourceKeys">本次值发生变化的源键（稳定排序）。</param>
/// <param name="SkippedSourceKeys">本次不可读而跳过的源键（稳定排序）。</param>
/// <param name="Issues">装配过程中的非致命问题（读取失败、超限截断等），禁止静默吞掉。</param>
public sealed record ContextAssemblyResult(
    string SystemBaseline,
    string? HistoryBaseline,
    string? DeltaText,
    IReadOnlyList<string> ChangedSourceKeys,
    IReadOnlyList<string> SkippedSourceKeys,
    IReadOnlyList<string> Issues);

/// <summary>
/// 上下文装配入口：唯一的"读源 + 规范化 + diff + 增量"编排点。
/// </summary>
public interface IContextAssembler
{
    /// <summary>
    /// 确保会话的上下文基线新鲜。
    /// </summary>
    /// <param name="session">目标会话（状态读写与增量消息都落在该实例上）。</param>
    /// <param name="mode">装配模式；<see cref="ContextAssemblyMode.ReadOnly"/> 不写回任何状态。</param>
    /// <param name="insertBeforeMessageId">增量消息的插入位置（插入到该消息之前）；null 表示追加到末尾。</param>
    /// <param name="skip">本轮不参与装配的源键；null 表示不跳过任何源。</param>
    /// <param name="ct">取消令牌。</param>
    Task<ContextAssemblyResult> EnsureFreshAsync(
        SessionData session,
        ContextAssemblyMode mode,
        string? insertBeforeMessageId,
        IReadOnlySet<ContextSourceKey>? skip,
        CancellationToken ct = default);

    /// <summary>
    /// 请求构建时的系统提示词附加文本（从已存状态读取，字节稳定）。不读源、不算 diff。
    /// </summary>
    string BuildSystemBaseline(SessionData session);
}
