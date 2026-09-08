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
    Pong,
    /// <summary>Channel Host → Server：声明本连接负责的 channelId</summary>
    ChannelRegister,
    /// <summary>Server → Channel Host：定时任务等主动出站</summary>
    ChannelOutbound
}
