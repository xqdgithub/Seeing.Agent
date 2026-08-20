using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.App.Execution;
using Seeing.Agent.Compression;

namespace Seeing.Agent.App.Execution;

/// <summary>
/// 压缩执行器：统一所有压缩入口（自动门控 / /compact 命令 / API）的事件序列，
/// 保证 Started 先于摘要生成的 Delta 增量发布，UI 进度状态与实时内容时序正确。
/// </summary>
public class CompactionRunner
{
    private readonly CompressionService _compressionService;
    private readonly IExecutionEventPublisher _publisher;

    public CompactionRunner(CompressionService compressionService, IExecutionEventPublisher publisher)
    {
        _compressionService = compressionService ?? throw new ArgumentNullException(nameof(compressionService));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
    }

    /// <summary>
    /// 执行压缩并发布 Started → Completed/Failed 事件序列。
    /// </summary>
    public async Task<CompressionOutcome> RunAsync(
        string sessionId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        // Started 必须在压缩开始前发布：LlmSummarizer 的 Delta 实时进度依赖 Started 建立 UI 进度态
        _publisher.Publish(sessionId, new CompactionStartedEvent
        {
            SessionId = sessionId,
            Reason = reason
        });

        var outcome = await _compressionService.CompressAsync(sessionId, reason, cancellationToken);

        if (outcome.Success)
        {
            _publisher.Publish(sessionId, new CompactionCompletedEvent
            {
                SessionId = sessionId,
                TokensBefore = outcome.TokensBefore,
                TokensAfter = outcome.TokensAfter,
                MessagesRemoved = outcome.MessagesRemoved,
                Summary = outcome.Summary
            });
        }
        else
        {
            _publisher.Publish(sessionId, new CompactionFailedEvent
            {
                SessionId = sessionId,
                ErrorMessage = outcome.ErrorMessage
            });
        }

        return outcome;
    }
}