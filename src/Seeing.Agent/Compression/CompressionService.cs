using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Summarization;
using Seeing.Session.Core;
using Seeing.Session.Management;

namespace Seeing.Agent.Compression;

/// <summary>
/// 压缩编排服务 - 唯一压缩执行入口（TokenBudget 只决策、本服务执行）
/// </summary>
public class CompressionService
{
    private readonly ISummarizer? _summarizer;
    private readonly ISessionManager _sessionManager;
    private readonly ILogger<CompressionService> _logger;

    public CompressionService(
        ISummarizer? summarizer,
        ISessionManager sessionManager,
        ILogger<CompressionService>? logger = null)
    {
        _summarizer = summarizer;
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _logger = logger ?? NullLogger<CompressionService>.Instance;
    }

    /// <summary>
    /// 执行压缩：摘要 → 写回历史 → 持久化
    /// </summary>
    public async Task<CompressionOutcome> CompressAsync(
        string sessionId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (_summarizer == null)
        {
            return new CompressionOutcome
            {
                Success = false,
                ErrorMessage = "未配置摘要器（ISummarizer），无法压缩"
            };
        }

        var session = await _sessionManager.GetOrLoadAsync(sessionId, cancellationToken);
        // 活跃消息（不含历史已压缩标记）作为压缩输入，与摘要器输入保持一致
        var messages = session.GetActiveMessages();
        if (messages.Count == 0)
        {
            return new CompressionOutcome { Success = false, ErrorMessage = "会话无消息，无需压缩" };
        }

        // 调用前快照被压缩段起点：摘要 LLM 耗时数秒，期间后台任务可能并发追加消息，
        // 若在 SummarizeAsync 之后才计算会错位（新消息被误归入已压缩历史）
        var activeStart = session.Messages.Count - messages.Count;

        try
        {
            var request = new SummarizeRequest(session.Id, Reason: reason);

            var result = await _summarizer.SummarizeAsync(request, cancellationToken);

            // 锚定摘要：将本次摘要写回会话上下文，供下次压缩合并更新
            session.SetContext(SummarizeRequest.LastSummaryContextKey, result.Summary);

            // 摘要消息插入到被压缩部分之后、保留消息之前；标记 IsSummary 供 UI 特殊展示。
            // 被压缩的旧消息不删除也不做标记：摘要消息的位置即压缩真相（摘要之前 = 已压缩历史，仍保留展示）
            // 思考过程一并保存（若摘要器返回），UI 摘要条可展开查看
            var summaryMessage = string.IsNullOrWhiteSpace(result.Reasoning)
                ? SessionMessage.AssistantMessage(result.Summary)
                : SessionMessage.AssistantMessageWithReasoning(result.Summary, result.Reasoning);
            summaryMessage.IsSummary = true;
            var insertIndex = activeStart + result.MessagesRemoved;
            // 并发保护：摘要期间可能被追加消息或重复压缩，校验插入索引仍落在合法范围，越界则就近回退
            if (insertIndex < 0) insertIndex = 0;
            if (insertIndex > session.Messages.Count) insertIndex = session.Messages.Count;
            session.Messages.Insert(insertIndex, summaryMessage);

            session.UpdatedAt = DateTime.Now;
            await _sessionManager.SaveAndNotifyAsync(session.Id, persist: true, cancellationToken);

            _logger.LogInformation("压缩完成: {SessionId} 移除 {Count} 条", sessionId, result.MessagesRemoved);

            return new CompressionOutcome
            {
                Success = true,
                TokensBefore = EstimateTokens(messages),
                TokensAfter = EstimateTokens(result.ResultMessages),
                MessagesRemoved = result.MessagesRemoved,
                Summary = result.Summary,
                Strategy = "LlmSummarizer"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "压缩失败: {SessionId}", sessionId);
            return new CompressionOutcome { Success = false, ErrorMessage = ex.Message };
        }
    }

    private static int EstimateTokens(IReadOnlyList<SessionMessage> messages)
    {
        // 粗估：1 token ≈ 4 字符，每条至少计 1（占位实现，后续可注入 ITokenCounter）
        return messages.Sum(m => Math.Max(1, (m.Content?.Length ?? 0) / 4));
    }
}