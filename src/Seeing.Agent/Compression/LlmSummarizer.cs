using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Summarization;
using Seeing.Agent.Llm;
using Seeing.Session.Core;

namespace Seeing.Agent.Compression;

/// <summary>
/// 默认摘要器 - 基于 ITextCompletion 生成会话摘要
/// </summary>
/// <remarks>
/// prompt/model/provider 选择由本类内部自决（用户决策：压缩是框架的事）
/// </remarks>
public class LlmSummarizer : ISummarizer
{
    private const string SystemPrompt =
        "你是一个会话压缩助手。将以下对话压缩为精炼摘要，保留关键事实、决策、结论与待办事项，不要遗漏用户的核心意图，不要添加对话历史中不存在的信息。直接输出摘要文本。";

    private readonly ITextCompletion _textCompletion;
    private readonly ILogger<LlmSummarizer> _logger;

    public LlmSummarizer(
        ITextCompletion textCompletion,
        ILogger<LlmSummarizer>? logger = null)
    {
        _textCompletion = textCompletion ?? throw new ArgumentNullException(nameof(textCompletion));
        _logger = logger ?? NullLogger<LlmSummarizer>.Instance;
    }

    /// <inheritdoc />
    public async Task<SummarizeResult> SummarizeAsync(
        SummarizeRequest request,
        CancellationToken cancellationToken = default)
    {
        var history = string.Join("\n", request.Messages.Select(m => $"{m.Role}: {m.Content}"));
        var userPrompt = $"""
            请压缩以下对话历史，输出精炼摘要：

            对话历史：
            {history}
            """;

        var summary = await _textCompletion.CompleteAsync(
            SystemPrompt,
            userPrompt,
            model: null,
            maxTokens: request.MaxOutputTokens,
            ct: cancellationToken);

        if (string.IsNullOrWhiteSpace(summary))
        {
            throw new InvalidOperationException("摘要生成失败：模型返回空内容");
        }

        var keepCount = Math.Min(request.KeepRecentCount, request.Messages.Count);
        var trimmed = request.Messages.Skip(request.Messages.Count - keepCount).ToList();

        return new SummarizeResult(
            Summary: summary,
            TrimmedMessages: trimmed,
            SummaryTokenCount: Math.Max(1, (summary.Length) / 4));
    }
}