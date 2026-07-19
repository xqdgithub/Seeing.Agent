namespace Seeing.Gateway.Protocol;

/// <summary>
/// Gateway WebSocket 帧类型
/// </summary>
public enum GatewayWsFrameType
{
    Connected,
    Submit,
    SubmitAck,
    ChatEvent,
    ExecutionComplete,
    ChatError,
    Cancel,
    CancelAck,
    PermissionRespond,
    PermissionAck,
    Error,
    Ping,
    Pong
}
