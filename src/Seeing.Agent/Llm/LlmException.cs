namespace Seeing.Agent.Llm;

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

    /// <summary>错误来源（client/network/timeout/cancellation）</summary>
    public string Source { get; init; } = "unknown";

    public LlmException(string message) : base(message) { }

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
    public TimeSpan Timeout { get; init; }

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

    public LlmRetryExhaustedException(int maxRetries, Exception? lastException)
        : base($"LLM 请求在 {maxRetries} 次重试后仍然失败", lastException)
    {
        Source = "retry_exhausted";
        MaxRetries = maxRetries;
        LastException = lastException;
        IsRetryable = false;
    }
}
