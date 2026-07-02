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

    public IReadOnlyList<GatewayContentPart>? Input { get; init; }

    public bool Stream { get; init; } = true;

    public Dictionary<string, object?>? Metadata { get; init; }
}
