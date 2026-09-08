namespace Seeing.Gateway.Models;

/// <summary>Gateway 会话重置结果</summary>
public sealed record GatewaySessionResetResult
{
    public required string SessionId { get; init; }

    public bool Cleared { get; init; }

    public int MessageCount { get; init; }
}
