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
    private readonly CompressionOptions _options;
    private readonly ILogger<CompressionService> _logger;

    public CompressionService(
        ISummarizer? summarizer,
        ISessionManager sessionManager,
        CompressionOptions options,
        ILogger<CompressionService>? logger = null)
    {
        _summarizer = summarizer;
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _options = options ?? new CompressionOptions();
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
        var messages = session.Messages.ToList();
        if (messages.Count == 0)
        {
            return new CompressionOutcome { Success = false, ErrorMessage = "会话无消息，无需压缩" };
        }

        try
        {
            var request = new SummarizeRequest(
                messages,
                MaxOutputTokens: _options.SummaryTargetTokens,
                KeepRecentCount: _options.KeepRecentMessages);

            var result = await _summarizer.SummarizeAsync(request, cancellationToken);

            session.Messages.Clear();
            session.Messages.AddRange(result.TrimmedMessages);
            session.UpdatedAt = DateTime.Now;
            await _sessionManager.SaveAndNotifyAsync(session.Id, persist: true, cancellationToken);

            _logger.LogInformation("压缩完成: {SessionId} 移除 {Count} 条", sessionId, messages.Count - result.TrimmedMessages.Count);

            return new CompressionOutcome
            {
                Success = true,
                TokensBefore = EstimateTokens(messages),
                TokensAfter = EstimateTokens(result.TrimmedMessages) + result.SummaryTokenCount,
                MessagesRemoved = messages.Count - result.TrimmedMessages.Count,
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