using System.Net;

namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// 共享重试判定（从 LlmService 迁出，供装饰器使用）。
/// </summary>
public static class LlmRetryPolicy
{
    public const string AttemptItemKey = "retry.attempt";
    public const string WillRetryItemKey = "retry.willRetry";
    public const string MaxRetriesItemKey = "retry.maxRetries";
    public const string NextDelayItemKey = "retry.nextDelayMs";

    /// <summary>
    /// 计算第 <paramref name="attempt"/> 次尝试失败后的退避延迟（attempt 从 1 开始），
    /// 指数增长并以 <paramref name="maxDelay"/> 封顶。
    /// 默认序列（base=500ms, cap=10s）：0.5, 1, 2, 4, 8, 10, 10…
    /// </summary>
    public static TimeSpan ComputeDelay(int attempt, TimeSpan baseDelay, TimeSpan maxDelay)
    {
        if (attempt < 1)
            attempt = 1;

        var milliseconds = Math.Min(
            baseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1),
            maxDelay.TotalMilliseconds);

        return TimeSpan.FromMilliseconds(milliseconds);
    }

    /// <summary>
    /// 判断是否应继续重试：次数上限（<paramref name="maxRetries"/> &gt; 0 时）与累计等待预算双重约束。
    /// </summary>
    public static bool ShouldRetry(
        int attempt,
        TimeSpan elapsed,
        TimeSpan nextDelay,
        int maxRetries,
        TimeSpan budget)
    {
        if (maxRetries > 0 && attempt >= maxRetries)
            return false;

        return elapsed + nextDelay <= budget;
    }

    public static bool IsRetryable(Exception ex, CancellationToken cancellationToken)
    {
        // 用户取消：不重试（含 TaskCanceledException）
        if (ex is OperationCanceledException oce &&
            cancellationToken.IsCancellationRequested &&
            (oce.CancellationToken == cancellationToken || oce.CancellationToken.IsCancellationRequested))
            return false;

        if (ex is HttpRequestException httpEx)
        {
            var status = httpEx.StatusCode;
            if (status is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized
                or HttpStatusCode.Forbidden or HttpStatusCode.NotFound
                or HttpStatusCode.UnprocessableEntity)
                return false;
            return true;
        }

        // 能力包自定义协议异常：只有它带 HTTP 状态码，须按同一口径判定。
        // 缺了这条，decision 的 429/5xx 会一次即抛——旧 SystemOne 自带的退避已被删除
        // （spec §7.7 要求统一由装饰器承担），不补这里等于该路径彻底失去重试。
        if (ex is DecisionProtocolException decisionEx)
            return IsRetryableStatusCode(decisionEx.StatusCode);

        // 对齐旧 IsTransientException：超时 CTS 触发的 OCE（token 未请求）可重试
        return ex is TimeoutException
            or IOException
            or OperationCanceledException;
    }

    /// <summary>
    /// 按 HTTP 状态码判定是否瞬时故障（可重试）。
    /// <para><c>0</c> 表示无响应的网络/超时失败——可重试；
    /// <c>408</c>/<c>429</c>/<c>5xx</c> 可重试；其余 4xx 属请求本身有问题，重试无意义。</para>
    /// </summary>
    public static bool IsRetryableStatusCode(int statusCode) => statusCode switch
    {
        0 or 408 or 429 => true,
        >= 500 and <= 599 => true,
        _ => false
    };
}
