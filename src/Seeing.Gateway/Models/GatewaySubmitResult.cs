namespace Seeing.Gateway.Models;

/// <summary>
/// Gateway Submit 结果（HTTP / Client 共用）
/// </summary>
public sealed class GatewaySubmitResult
{
    public bool Success { get; init; }

    public string SessionId { get; init; } = "";

    public string? ExecutionId { get; init; }

    public int QueuePosition { get; init; }

    public string? Error { get; init; }

    public static GatewaySubmitResult Succeeded(string sessionId, string executionId, int queuePosition = 0) => new()
    {
        Success = true,
        SessionId = sessionId,
        ExecutionId = executionId,
        QueuePosition = queuePosition
    };

    public static GatewaySubmitResult Failed(string sessionId, string error) => new()
    {
        Success = false,
        SessionId = sessionId,
        Error = error
    };
}
