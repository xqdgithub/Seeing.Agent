using Seeing.Session.Core;

namespace Seeing.Agent.Abstractions.Summarization;

/// <summary>
/// 摘要请求参数
/// <para>请求只描述摘要任务本身：对哪个会话、以什么原因做摘要。
/// 消息加载、保留策略、输出约束、模型选择、锚定摘要等细节由实现方依据 <see cref="SessionId"/> 自行自决（对齐 opencode compaction 服务设计），不进入抽象请求。</para>
/// </summary>
public sealed record SummarizeRequest(
    string SessionId,
    string Reason = "auto")
{
    /// <summary>会话上下文键：上次压缩摘要（供下次压缩锚定更新，对齐 opencode anchored summary）</summary>
    public const string LastSummaryContextKey = "LastCompactionSummary";
}