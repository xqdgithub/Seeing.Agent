namespace Seeing.Agent.Abstractions.Interactions;

/// <summary>宿主能力调用响应（宿主 → 服务端）。</summary>
public sealed class HostResponse
{
    /// <summary>对应请求标识。</summary>
    public string RequestId { get; set; } = string.Empty;

    /// <summary>归属会话。</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>结果状态。</summary>
    public HostResponseStatus Status { get; set; } = HostResponseStatus.Unavailable;

    /// <summary>结果载荷（可空）。</summary>
    public JsonElement? Payload { get; init; }

    /// <summary>失败/兜底原因（可空）。</summary>
    public string? Reason { get; init; }
}
