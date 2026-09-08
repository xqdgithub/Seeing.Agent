using Seeing.Session.Core;

namespace Seeing.Agent.Abstractions.Summarization;

/// <summary>
/// 摘要结果
/// <para><see cref="ResultMessages"/> 为压缩后的新历史（摘要消息 + 保留的最后一轮消息）。
/// 被压缩的旧消息不删除、不做标记：摘要消息的位置即压缩真相（摘要之前的消息由调用方保留展示、不再传递给 LLM）。</para>
/// </summary>
public sealed record SummarizeResult(
    string Summary,
    IReadOnlyList<SessionMessage> ResultMessages,
    int SummaryTokenCount,
    int MessagesRemoved,
    string? Reasoning = null);