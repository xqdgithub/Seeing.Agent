namespace Seeing.Gateway.Client;

/// <summary>
/// Gateway HTTP 客户端配置
/// </summary>
public class GatewayClientOptions
{
    public const string SectionName = "Gateway";

    /// <summary>Gateway 服务根地址</summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:8765";

    /// <summary>HTTP 超时（含 SSE 长连接读取）</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>可选 API Key，通过 X-Api-Key 头发送</summary>
    public string? ApiKey { get; set; }

    /// <summary>传输方式：HttpSse | WebSocket</summary>
    public GatewayClientTransport Transport { get; set; } = GatewayClientTransport.HttpSse;

    /// <summary>WebSocket 路径（Transport=WebSocket 时使用）</summary>
    public string WebSocketPath { get; set; } = "/api/gateway/ws";
}
