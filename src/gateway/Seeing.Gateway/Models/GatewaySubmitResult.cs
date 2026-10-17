namespace Seeing.Gateway.Models;

/// <summary>
/// Gateway Submit 结果（HTTP / Client 共用）
/// </summary>
public sealed class GatewaySubmitResult
{
    public bool Success { get; init; }

    public string SessionId { get; init; } = "";

    /// <summary>submit 帧关联 ID（WebSocket 传输下由提交方生成、ack 帧回填；用于请求/应答流按轮次关联）</summary>
    public string? RequestId { get; init; }

    /// <summary>会话未消费输入数（原 QueuePosition，改会话级后不再表示执行队列位次）。</summary>
    public int PendingInputCount { get; init; }

    public string? Error { get; init; }

    public static GatewaySubmitResult Succeeded(string sessionId, int pendingInputCount = 0) => new()
    {
        Success = true,
        SessionId = sessionId,
        PendingInputCount = pendingInputCount
    };

    public static GatewaySubmitResult Failed(string sessionId, string error) => new()
    {
        Success = false,
        SessionId = sessionId,
        Error = error
    };
}
