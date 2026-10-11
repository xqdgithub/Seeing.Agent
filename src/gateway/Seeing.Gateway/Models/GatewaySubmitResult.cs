namespace Seeing.Gateway.Models;

/// <summary>
/// Gateway Submit 结果（HTTP / Client 共用）
/// </summary>
public sealed class GatewaySubmitResult
{
    public bool Success { get; init; }

    public string SessionId { get; init; } = "";

    public string? ExecutionId { get; init; }

    /// <summary>会话未消费输入数（原 QueuePosition，改会话级后不再表示执行队列位次）。</summary>
    public int PendingInputCount { get; init; }

    public string? Error { get; init; }

    public static GatewaySubmitResult Succeeded(string sessionId, string? executionId, int pendingInputCount = 0) => new()
    {
        Success = true,
        SessionId = sessionId,
        ExecutionId = executionId,
        PendingInputCount = pendingInputCount
    };

    public static GatewaySubmitResult Failed(string sessionId, string error) => new()
    {
        Success = false,
        SessionId = sessionId,
        Error = error
    };
}
