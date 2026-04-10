using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace Seeing.Agent.Core.Questions;

/// <summary>
/// 问题管理器 - 线程安全的问题等待机制实现
/// </summary>
public class QuestionManager : IQuestionManager
{
    private readonly ILogger<QuestionManager> _logger;
    private readonly ConcurrentDictionary<string, PendingQuestion> _pending = new();
    private readonly ConcurrentQueue<QuestionRequest> _pendingList = new();

    /// <summary>
    /// 创建 QuestionManager 实例
    /// </summary>
    /// <param name="logger">日志器</param>
    public QuestionManager(ILogger<QuestionManager> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<QuestionResult> AskAsync(QuestionRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (string.IsNullOrEmpty(request.Id))
        {
            request.Id = GenerateQuestionId();
        }

        _logger.LogInformation("提问: Id={Id}, QuestionsCount={Count}", 
            request.Id, request.Questions.Count);

        var completionSource = new TaskCompletionSource<QuestionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var pending = new PendingQuestion
        {
            Request = request,
            CompletionSource = completionSource
        };

        _pending[request.Id] = pending;
        _pendingList.Enqueue(request);

        // 注册取消回调
        using var registration = cancellationToken.Register(() =>
        {
            if (_pending.TryRemove(request.Id, out var removed))
            {
                _logger.LogWarning("问题被取消: Id={Id}", request.Id);
                removed.CompletionSource.TrySetCanceled(cancellationToken);
            }
        });

        try
        {
            var result = await completionSource.Task;
            
            _logger.LogInformation("收到回答: Id={Id}, AnswersCount={Count}", 
                request.Id, result.Answers.Count);

            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("问题等待超时或被取消: Id={Id}", request.Id);
            throw;
        }
        finally
        {
            _pending.TryRemove(request.Id, out _);
        }
    }

    /// <inheritdoc/>
    public Task AnswerAsync(string requestId, List<QuestionAnswer> answers, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(requestId))
        {
            throw new ArgumentException("requestId 不能为空", nameof(requestId));
        }

        if (!_pending.TryRemove(requestId, out var pending))
        {
            _logger.LogWarning("回答未知请求: Id={Id}", requestId);
            return Task.CompletedTask;
        }

        var result = new QuestionResult
        {
            RequestId = requestId,
            Answers = answers ?? new List<QuestionAnswer>()
        };

        _logger.LogInformation("提交回答: Id={Id}, AnswersCount={Count}", 
            requestId, result.Answers.Count);

        pending.CompletionSource.TrySetResult(result);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task RejectAsync(string requestId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(requestId))
        {
            throw new ArgumentException("requestId 不能为空", nameof(requestId));
        }

        if (!_pending.TryRemove(requestId, out var pending))
        {
            _logger.LogWarning("拒绝未知请求: Id={Id}", requestId);
            return Task.CompletedTask;
        }

        _logger.LogInformation("拒绝问题: Id={Id}", requestId);

        pending.CompletionSource.TrySetException(new QuestionRejectedException());
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<QuestionRequest>> GetPendingAsync()
    {
        var pending = new List<QuestionRequest>();
        
        // 只返回仍在等待的请求
        foreach (var kvp in _pending)
        {
            pending.Add(kvp.Value.Request);
        }

        return Task.FromResult<IReadOnlyList<QuestionRequest>>(pending);
    }

    /// <summary>
    /// 生成问题 ID
    /// </summary>
    private static string GenerateQuestionId()
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
        var random = Guid.NewGuid().ToString("N").Substring(0, 8);
        return $"q_{timestamp}_{random}";
    }

    /// <summary>
    /// 待处理问题内部结构
    /// </summary>
    private class PendingQuestion
    {
        public QuestionRequest Request { get; set; } = new();
        public TaskCompletionSource<QuestionResult> CompletionSource { get; set; } = new();
    }
}