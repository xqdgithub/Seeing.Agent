namespace Seeing.Agent.Abstractions.Interactions;

/// <summary>宿主能力调用请求（服务端 → 宿主）。</summary>
public sealed class HostRequest
{
    /// <summary>请求标识（未提供时由在途管理器分配）。</summary>
    public string RequestId { get; set; } = string.Empty;

    /// <summary>归属会话。</summary>
    public required string SessionId { get; init; }

    /// <summary>关联的工具调用标识（可空）。</summary>
    public string? CallId { get; init; }

    /// <summary>能力标识，如 "editor.command"。</summary>
    public required string Kind { get; init; }

    /// <summary>载荷（一律经 JsonSerializer.SerializeToElement 构造，自持 JsonDocument）。</summary>
    public JsonElement? Payload { get; init; }
}
