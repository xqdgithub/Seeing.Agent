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

    /// <summary>关联的工具调用 ID（内联卡片关联键）</summary>
    public string? CallId { get; init; }

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

    /// <summary>权限请求允许的动作集合（once | session | sessionDirectory）</summary>
    public IReadOnlyList<string>? PermissionAllowedScopes { get; init; }

    /// <summary>权限决策：allow | deny</summary>
    public string? PermissionDecision { get; init; }

    /// <summary>权限决策作用域：once | session | sessionDirectory</summary>
    public string? PermissionScope { get; init; }

    /// <summary>权限决策来源：user | policy | cancellation | timeout | noChannel</summary>
    public string? PermissionResolvedBy { get; init; }

    /// <summary>权限决策原因</summary>
    public string? PermissionReason { get; init; }

    /// <summary>工具调用标题</summary>
    public string? ToolTitle { get; init; }

    /// <summary>Loop 取消时已完成的步数</summary>
    public int? CompletedSteps { get; init; }
}
