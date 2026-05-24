namespace Seeing.Agent.MCP.Core;

public sealed class McpOperationResult
{
    public bool Success { get; init; }
    public string ServerName { get; init; }
    public McpOperationType OperationType { get; init; }
    public McpConnectionState? Status { get; init; }
    public McpErrorInfo? Error { get; init; }
    public TimeSpan Duration { get; init; }

    /// <summary>操作详情信息（如警告、跳过原因等）</summary>
    public IReadOnlyDictionary<string, object>? Details { get; init; }

    private McpOperationResult(
        bool success,
        string serverName,
        McpOperationType operationType,
        McpConnectionState? status = null,
        McpErrorInfo? error = null,
        TimeSpan? duration = null)
    {
        Success = success;
        ServerName = serverName;
        OperationType = operationType;
        Status = status;
        Error = error;
        Duration = duration ?? TimeSpan.Zero;
    }

    public static McpOperationResult Succeeded(
        string serverName,
        McpOperationType operationType,
        McpConnectionState? status = null,
        TimeSpan? duration = null)
        => new(true, serverName, operationType, status, null, duration);

    public static McpOperationResult Failed(
        string serverName,
        McpOperationType operationType,
        McpErrorInfo error,
        McpConnectionState? status = null,
        TimeSpan? duration = null)
        => new(false, serverName, operationType, status, error, duration);

    public static McpOperationResult NoChange(
        string serverName,
        McpOperationType operationType,
        McpConnectionState status,
        TimeSpan? duration = null)
        => new(true, serverName, operationType, status, null, duration);

    /// <summary>
    /// 创建带详情的成功结果
    /// </summary>
    public static McpOperationResult SucceededWithDetails(
        string serverName,
        McpOperationType operationType,
        IReadOnlyDictionary<string, object> details,
        McpConnectionState? status = null,
        TimeSpan? duration = null)
        => new(true, serverName, operationType, status, null, duration)
        {
            Details = details
        };

    /// <summary>
    /// 返回带详情的新实例
    /// </summary>
    public McpOperationResult WithDetails(IReadOnlyDictionary<string, object> details)
        => new(Success, ServerName, OperationType, Status, Error, Duration)
        {
            Details = details
        };
}