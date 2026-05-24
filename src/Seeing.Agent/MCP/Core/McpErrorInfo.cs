namespace Seeing.Agent.MCP.Core;

public sealed class McpErrorInfo
{
    public McpErrorCode Code { get; init; }
    public string? TechnicalDetail { get; init; }
    public string UserMessage { get; init; }
    public string? RecoveryHint { get; init; }
    public Exception? InnerException { get; init; }
    public bool IsTransient { get; init; }
    public bool RequiresUserAction { get; init; }

    private McpErrorInfo(
        McpErrorCode code,
        string userMessage,
        string? technicalDetail = null,
        string? recoveryHint = null,
        Exception? innerException = null,
        bool isTransient = false,
        bool requiresUserAction = false)
    {
        Code = code;
        UserMessage = userMessage;
        TechnicalDetail = technicalDetail;
        RecoveryHint = recoveryHint;
        InnerException = innerException;
        IsTransient = isTransient;
        RequiresUserAction = requiresUserAction;
    }

    public static McpErrorInfo ConnectionTimeout(string serverName, TimeSpan timeout, Exception? innerException = null)
        => new(McpErrorCode.ConnectionTimeout,
            $"服务器 '{serverName}' 连接超时（{timeout.TotalSeconds}秒）",
            $"连接超时: {timeout}",
            "请检查网络连接或增加超时时间",
            innerException,
            isTransient: true);

    public static McpErrorInfo AuthenticationFailed(string serverName, string? reason = null)
        => new(McpErrorCode.AuthenticationFailed,
            $"服务器 '{serverName}' 认证失败",
            reason,
            "请检查认证凭据是否正确",
            requiresUserAction: true);

    public static McpErrorInfo SessionExpired(string serverName)
        => new(McpErrorCode.SessionExpired,
            $"服务器 '{serverName}' 会话已过期",
            recoveryHint: "请重新连接或更新认证令牌",
            isTransient: true);

    public static McpErrorInfo ProcessCrashed(string serverName, int? exitCode = null)
        => new(McpErrorCode.ProcessCrashed,
            $"服务器 '{serverName}' 进程已崩溃",
            exitCode.HasValue ? $"退出码: {exitCode}" : null,
            "请检查服务器配置和日志",
            isTransient: true);

    public static McpErrorInfo ConfigInvalid(string serverName, string reason)
        => new(McpErrorCode.ConfigInvalid,
            $"服务器 '{serverName}' 配置无效: {reason}",
            reason,
            "请修正配置后重试",
            requiresUserAction: true);

    public static McpErrorInfo ReconnectExhausted(string serverName, int maxAttempts)
        => new(McpErrorCode.ReconnectExhausted,
            $"服务器 '{serverName}' 重连次数已达上限（{maxAttempts}次）",
            $"最大重连次数: {maxAttempts}",
            "请检查服务器状态后手动重连",
            requiresUserAction: true);

    public static McpErrorInfo ServerPaused(string serverName)
        => new(McpErrorCode.ServerPaused,
            $"服务器 '{serverName}' 已暂停",
            recoveryHint: "请先恢复服务器后再操作");

    public static McpErrorInfo OperationCancelled(string serverName, McpOperationType operation)
        => new(McpErrorCode.OperationCancelled,
            $"服务器 '{serverName}' 的 {operation} 操作已取消",
            $"操作类型: {operation}",
            isTransient: true);

    public static McpErrorInfo ConfigMissing(string serverName)
        => new(McpErrorCode.ConfigMissing,
            $"服务器 '{serverName}' 配置缺失",
            null,
            "请提供服务器配置后重试",
            requiresUserAction: true);

    public static McpErrorInfo ServerRemoved(string serverName)
        => new(McpErrorCode.ServerRemoved,
            $"服务器 '{serverName}' 已被移除",
            null,
            "无法操作已移除的服务器");

    public static McpErrorInfo FromException(McpErrorCode code, Exception ex, string? serverName = null)
        => new(code,
            serverName != null ? $"服务器 '{serverName}' 发生错误: {ex.Message}" : ex.Message,
            ex.ToString(),
            "请检查异常详情并重试",
            ex,
            isTransient: code == McpErrorCode.ConnectionTimeout
                || code == McpErrorCode.NetworkError
                || code == McpErrorCode.ToolExecutionError);
}