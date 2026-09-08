using Seeing.Session.Core;

namespace Seeing.Agent.Abstractions.Summarization;

/// <summary>
/// 会话摘要器 - 唯一公共压缩抽象（插件可替换默认 LlmSummarizer）
/// </summary>
public interface ISummarizer
{
    /// <summary>
    /// 生成摘要并返回压缩后的消息列表
    /// </summary>
    Task<SummarizeResult> SummarizeAsync(SummarizeRequest request, CancellationToken cancellationToken = default);
}