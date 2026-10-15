using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Interactions;
using Seeing.Agent.Abstractions.Questions;
using Seeing.Agent.Core.Interactions;

namespace Seeing.Agent.Core.Questions;

/// <summary>
/// 问答在途请求管理器（Singleton）：问答请求/结果事件的唯一发布点；
/// 监听可呈现性注册表注销信号，对不可呈现会话的在途请求以 Unavailable 收敛。
/// </summary>
public sealed class QuestionRequestManager
    : PendingRequestManager<QuestionRequest, QuestionResult>, IQuestionRequestManager
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    private readonly IExecutionEventPublisher? _eventPublisher;
    private readonly ILogger<QuestionRequestManager>? _logger;
    private readonly TimeSpan _timeout;

    /// <summary>创建问答在途管理器（等待超时 10 分钟；收敛宽限 1 秒）。</summary>
    public QuestionRequestManager(
        IExecutionEventPublisher? eventPublisher,
        IQuestionSurfaceRegistry? surfaceRegistry,
        IServiceProvider? serviceProvider,
        ILogger<QuestionRequestManager>? logger)
        : this(
            eventPublisher,
            surfaceRegistry ?? serviceProvider?.GetService(typeof(IQuestionSurfaceRegistry)) as IQuestionSurfaceRegistry,
            logger,
            DefaultTimeout,
            null)
    {
    }

    /// <summary>创建问答在途管理器（可指定等待超时与收敛宽限；仅供测试注入短值）。</summary>
    internal QuestionRequestManager(
        IExecutionEventPublisher? eventPublisher,
        IQuestionSurfaceRegistry? surfaceRegistry,
        ILogger<QuestionRequestManager>? logger,
        TimeSpan timeout,
        TimeSpan? convergenceGrace = null)
        : base(logger, surfaceRegistry, convergenceGrace)
    {
        _eventPublisher = eventPublisher;
        _logger = logger;
        _timeout = timeout;
    }

    /// <inheritdoc />
    protected override TimeSpan Timeout => _timeout;

    /// <inheritdoc />
    protected override string GetRequestId(QuestionRequest request) => request.Id;

    /// <inheritdoc />
    protected override QuestionRequest AssignRequestId(QuestionRequest request, string id)
    {
        request.Id = id;
        return request;
    }

    /// <inheritdoc />
    protected override string GetSessionId(QuestionRequest request) => request.SessionId;

    /// <inheritdoc />
    protected override QuestionResult CreateFallback(QuestionRequest request, PendingFallbackReason reason) =>
        new()
        {
            RequestId = request.Id,
            Status = reason switch
            {
                PendingFallbackReason.Timeout => QuestionResultStatus.Timeout,
                PendingFallbackReason.Cancelled => QuestionResultStatus.Cancelled,
                PendingFallbackReason.Disposed => QuestionResultStatus.Unavailable,
                PendingFallbackReason.Unavailable => QuestionResultStatus.Unavailable,
                _ => QuestionResultStatus.Unavailable
            }
        };

    /// <inheritdoc />
    protected override QuestionResult CreateMissingResponse(RequestTicket ticket) =>
        new()
        {
            RequestId = ticket.RequestId,
            Status = QuestionResultStatus.Unavailable
        };

    /// <inheritdoc />
    protected override void OnRequestBegan(QuestionRequest request) =>
        PublishSafe(request.SessionId, new QuestionRequestEvent
        {
            SessionId = request.SessionId,
            RequestId = request.Id,
            CallId = request.Tool?.CallId,
            Questions = request.Questions
        });

    /// <inheritdoc />
    protected override void OnResolved(QuestionRequest request, QuestionResult response) =>
        PublishSafe(request.SessionId, new QuestionResolvedEvent
        {
            SessionId = request.SessionId,
            RequestId = request.Id,
            CallId = request.Tool?.CallId,
            Status = response.Status,
            Answers = response.Answers
        });

    private void PublishSafe(string sessionId, IMessageEvent evt)
    {
        var publisher = _eventPublisher;
        if (publisher is null)
            return;

        try
        {
            publisher.Publish(sessionId, evt);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "发布问答事件失败 Type={EventType} SessionId={SessionId}", evt.Type, sessionId);
        }
    }
}
