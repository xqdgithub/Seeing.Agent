using Seeing.Session.Core;

namespace Seeing.Agent.Abstractions.Summarization;

/// <summary>
/// 摘要结果（Summary 写入 System 占位；TrimmedMessages 替换原历史）
/// </summary>
public sealed record SummarizeResult(
    string Summary,
    IReadOnlyList<SessionMessage> TrimmedMessages,
    int SummaryTokenCount);