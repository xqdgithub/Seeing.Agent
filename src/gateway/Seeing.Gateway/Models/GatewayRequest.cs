namespace Seeing.Gateway.Models;

/// <summary>
/// 网关聊天请求
/// </summary>
public record GatewayRequest
{
    public required string SessionId { get; init; }

    public string? UserId { get; init; }

    public string? ChannelId { get; init; }

    public string? AgentId { get; init; }

    public string? ModelId { get; init; }

    /// <summary>思考强度档位 key（可选；映射到 ChatOptions.ThinkingEffort）</summary>
    public string? ThinkingEffort { get; init; }

    /// <summary>ACP 透传 session mode；Native Agent 忽略。</summary>
    public string? ModeId { get; init; }

    public IReadOnlyList<GatewayContentPart>? Input { get; init; }

    /// <summary>用户引用的消息上下文（与用户当前 Input 并列，由 Gateway Server 合成 user turn）</summary>
    public GatewayQuoteContext? Quote { get; init; }

    public bool Stream { get; init; } = true;

    public Dictionary<string, object?>? Metadata { get; init; }
}
