using Seeing.Session.Core;

namespace Seeing.Agent.Abstractions.Summarization;

/// <summary>
/// 摘要请求参数
/// </summary>
public sealed record SummarizeRequest(
    IReadOnlyList<SessionMessage> Messages,
    int MaxOutputTokens,
    int KeepRecentCount);