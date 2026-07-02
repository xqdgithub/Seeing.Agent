namespace Seeing.Gateway.Models;

/// <summary>
/// 网关事件载荷：增量文本、工具信息、权限请求等
/// </summary>
public record GatewayEventData
{
    /// <summary>是否为增量更新（流式 delta）</summary>
    public bool Delta { get; init; }

    public string? Text { get; init; }

    public string? Reasoning { get; init; }

    public string? Role { get; init; }

    public string? UserInput { get; init; }

    public string? ToolCallId { get; init; }

    public string? ToolName { get; init; }

    public object? ToolArguments { get; init; }

    public string? ToolStatus { get; init; }

    public string? ToolOutput { get; init; }

    public string? ToolError { get; init; }

    public string? PermissionId { get; init; }

    public string? PermissionKind { get; init; }

    public string? Resource { get; init; }

    public string? PermissionMessage { get; init; }

    public string? RiskLevel { get; init; }

    public string? Error { get; init; }

    public string? ErrorSource { get; init; }

    public int? TotalSteps { get; init; }

    public bool? Success { get; init; }

    public string? CancelReason { get; init; }

    /// <summary>流式轮次索引（StreamStart）</summary>
    public int? Step { get; init; }

    /// <summary>流种类：content | reasoning | tool</summary>
    public string? StreamKind { get; init; }

    /// <summary>消息角色：assistant | tool | user | system</summary>
    public string? MessageRole { get; init; }

    /// <summary>执行耗时</summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>Token 使用统计</summary>
    public GatewayTokenUsage? Usage { get; init; }

    /// <summary>权限请求参数</summary>
    public object? PermissionArguments { get; init; }

    /// <summary>权限决策：allow | deny</summary>
    public string? PermissionDecision { get; init; }

    /// <summary>权限决策原因</summary>
    public string? PermissionReason { get; init; }

    /// <summary>工具调用标题</summary>
    public string? ToolTitle { get; init; }

    /// <summary>Loop 取消时已完成的步数</summary>
    public int? CompletedSteps { get; init; }

    /// <summary>子代理名称</summary>
    public string? SubAgentName { get; init; }

    /// <summary>子会话 ID</summary>
    public string? SubSessionId { get; init; }
}
