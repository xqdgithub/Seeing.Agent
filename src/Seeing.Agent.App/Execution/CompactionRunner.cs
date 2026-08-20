using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.App.Execution;
using Seeing.Agent.Compression;
using Microsoft.Extensions.Logging;
using Seeing.Session.Core;
using Seeing.Session.Management;

namespace Seeing.Agent.App.Execution;

/// <summary>
/// 压缩执行器：统一所有压缩入口（自动门控 / /compact 命令 / API）的事件序列，
/// 保证 Started 先于摘要生成的 Delta 增量发布，UI 进度状态与实时内容时序正确。
/// </summary>
public class CompactionRunner
{
    private readonly CompressionService _compressionService;
    private readonly IExecutionEventPublisher _publisher;
    private readonly ISessionManager _sessionManager;
    private readonly ILogger<CompactionRunner> _logger;

    public CompactionRunner(
        CompressionService compressionService,
        IExecutionEventPublisher publisher,
        ISessionManager sessionManager,
        ILogger<CompactionRunner>? logger = null)
    {
        _compressionService = compressionService ?? throw new ArgumentNullException(nameof(compressionService));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<CompactionRunner>.Instance;
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
            // 失败信息落为会话系统消息，持久化 + 通知（UI 时间线可见，刷新后保留）；
            // 先写会话再发布事件，保证 UI 收到事件时读到的是已写入的 SessionData
            try
            {
                var session = _sessionManager.Get(sessionId);
                if (session != null)
                {
                    // 移除历史压缩失败消息，防止失败累积污染后续 LLM 上下文
                    session.Messages.RemoveAll(m =>
                        m.Role == MessageRole.System &&
                        (m.Content?.StartsWith("压缩失败", StringComparison.Ordinal) ?? false));

                    var message = string.IsNullOrWhiteSpace(outcome.ErrorMessage)
                        ? "压缩失败"
                        : $"压缩失败: {outcome.ErrorMessage}";
                    session.Messages.Add(SessionMessage.SystemMessage(message));
                    await _sessionManager.SaveAndNotifyAsync(sessionId);
                }
            }
            catch (Exception ex)
            {
                // 失败写入不阻断事件发布：避免执行被标记失败而丢失压缩失败提示
                _logger.LogError(ex, "写入压缩失败系统消息失败: {SessionId}", sessionId);
            }

            _publisher.Publish(sessionId, new CompactionFailedEvent
            {
                SessionId = sessionId,
                ErrorMessage = outcome.ErrorMessage
            });
        }

        return outcome;
    }
}