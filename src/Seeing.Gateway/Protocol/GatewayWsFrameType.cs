namespace Seeing.Gateway.Protocol;

/// <summary>
/// Gateway WebSocket 帧类型
/// </summary>
public enum GatewayWsFrameType
{
    Connected,
    Chat,
    ChatEvent,
    ChatComplete,
    ChatError,
    Stop,
    StopAck,
    PermissionRespond,
    PermissionAck,
    Error,
    Ping,
    Pong
}
