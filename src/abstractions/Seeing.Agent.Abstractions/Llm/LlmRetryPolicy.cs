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

        // 对齐旧 IsTransientException：超时 CTS 触发的 OCE（token 未请求）可重试
        return ex is TimeoutException
            or IOException
            or OperationCanceledException;
    }
}
