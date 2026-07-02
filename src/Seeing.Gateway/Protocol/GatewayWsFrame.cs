using System.Text.Json;
using System.Text.Json.Serialization;

namespace Seeing.Gateway.Protocol;

/// <summary>
/// Gateway WebSocket JSON Text Frame 信封
/// </summary>
public record GatewayWsFrame
{
    public required GatewayWsFrameType Type { get; init; }

    /// <summary>请求关联 ID（chat/stop 用）</summary>
    public string? Id { get; init; }

    /// <summary>类型相关 body</summary>
    public JsonElement? Payload { get; init; }
}

/// <summary>
/// WS 帧 JSON 序列化
/// </summary>
public static class GatewayWsFrameSerializer
{
    public static JsonSerializerOptions JsonOptions { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    public static string Serialize(GatewayWsFrame frame) =>
        JsonSerializer.Serialize(frame, JsonOptions);

    public static GatewayWsFrame? Deserialize(string json) =>
        JsonSerializer.Deserialize<GatewayWsFrame>(json, JsonOptions);

    public static GatewayWsFrame Create(GatewayWsFrameType type, string? id = null, object? payload = null) =>
        new()
        {
            Type = type,
            Id = id,
            Payload = payload == null ? null : JsonSerializer.SerializeToElement(payload, JsonOptions)
        };
}

/// <summary>
/// chat.complete 帧 payload
/// </summary>
public record GatewayChatCompletePayload
{
    public required string SessionId { get; init; }

    public string? LoopId { get; init; }
}

/// <summary>
/// connected 握手帧 payload
/// </summary>
public record GatewayConnectedPayload
{
    public string ServerVersion { get; init; } = "1.0";

    public IReadOnlyList<string> Capabilities { get; init; } = ["chat", "stop", "permission"];
}

/// <summary>
/// 连接级或 chat 错误 payload
/// </summary>
public record GatewayWsErrorPayload
{
    public required string Message { get; init; }

    public string? Code { get; init; }
}

/// <summary>
/// stop 帧 payload
/// </summary>
public record GatewayStopPayload
{
    public required string SessionId { get; init; }
}

/// <summary>
/// stop.ack 帧 payload
/// </summary>
public record GatewayStopAckPayload
{
    public required string SessionId { get; init; }

    public bool Stopped { get; init; }
}

/// <summary>
/// permission.respond 帧 payload
/// </summary>
public record GatewayPermissionRespondPayload
{
    public required string SessionId { get; init; }

    public required string PermissionId { get; init; }

    public bool Allow { get; init; }

    public string? Reason { get; init; }
}
