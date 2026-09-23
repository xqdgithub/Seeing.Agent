using Seeing.Agent.Abstractions.Llm;
namespace Seeing.Agent.Core.Llm;

/// <summary>
/// LLM 调用异常基类 - 统一包装所有 LLM 相关异常
/// </summary>
public class LlmException : Exception
{
    /// <summary>模型 ID</summary>
    public string? ModelId { get; init; }

    /// <summary>Provider ID</summary>
    public string? ProviderId { get; init; }

    /// <summary>是否可重试</summary>
    public bool IsRetryable { get; init; }

    /// <summary>重试次数</summary>
    public int RetryCount { get; init; }

    /// <summary>
    /// 以消息构造 LLM 异常，Source 标记为 unknown。
    /// </summary>
public LlmException(string message) : base(message)
    {
        Source = "unknown";
    }

    /// <summary>
    /// 以消息与内部异常构造 LLM 异常。
    /// </summary>
    public LlmException(string message, Exception innerException)
        : base(message, innerException) { }

    /// <summary>
    /// 判断异常是否为可重试的瞬态故障
    /// </summary>
    public static bool IsTransientException(Exception ex)
    {
        return ex is TimeoutException
            || ex is HttpRequestException
            || ex is IOException
            // TaskCanceledException 仅当非用户主动取消时才重试
            || ex is OperationCanceledException oce && !oce.CancellationToken.IsCancellationRequested;
    }
}

/// <summary>
/// LLM 连接异常 - 网络层错误
/// </summary>
public class LlmConnectionException : LlmException
{
    /// <summary>
    /// 构造网络连接层异常，Source 为 network，并按内部异常判定可重试性。
    /// </summary>
    public LlmConnectionException(string message, Exception innerException)
        : base(message, innerException)
    {
        Source = "network";
        IsRetryable = IsTransientException(innerException);
    }
}

/// <summary>
/// LLM 超时异常
/// </summary>
public class LlmTimeoutException : LlmException
{
    /// <summary>触发超时的时间预算</summary>
    public TimeSpan Timeout { get; init; }

    /// <summary>
    /// 构造超时异常，Source 为 timeout，固定可重试并记录超时时长。
    /// </summary>
    public LlmTimeoutException(TimeSpan timeout, Exception innerException)
        : base($"LLM 请求超时 ({timeout.TotalSeconds:F1}s)", innerException)
    {
        Source = "timeout";
        Timeout = timeout;
        IsRetryable = true;
    }
}

/// <summary>
/// LLM 流式处理异常
/// </summary>
public class LlmStreamingException : LlmException
{
    /// <summary>已接收的部分内容</summary>
    public string? PartialContent { get; init; }

    /// <summary>流式阶段（reading/parsing/yielding）</summary>
    public string Stage { get; init; } = "unknown";

    /// <summary>
    /// 构造流式处理异常，Source 为 streaming，并按内部异常判定可重试性。
    /// </summary>
    public LlmStreamingException(string message, Exception innerException)
        : base(message, innerException)
    {
        Source = "streaming";
        IsRetryable = IsTransientException(innerException);
    }
}

/// <summary>
/// LLM 重试耗尽异常
/// </summary>
public class LlmRetryExhaustedException : LlmException
{
    /// <summary>最大重试次数</summary>
    public int MaxRetries { get; init; }

    /// <summary>最后一次异常</summary>
    public Exception? LastException { get; init; }

    /// <summary>
    /// 构造重试耗尽异常，携带最大重试次数与最后一次异常，标记为不可重试。
    /// </summary>
    public LlmRetryExhaustedException(int maxRetries, Exception? lastException)
        : base($"LLM 请求在 {maxRetries} 次重试后仍然失败", lastException!)
    {
        Source = "retry_exhausted";
        MaxRetries = maxRetries;
        LastException = lastException;
        IsRetryable = false;
    }
}
