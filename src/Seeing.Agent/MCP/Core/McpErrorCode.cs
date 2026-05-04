namespace Seeing.Agent.MCP.Core;

public enum McpErrorCode
{
    None,
    ConnectionTimeout,
    ConnectionRefused,
    HostUnreachable,
    NetworkError,
    AuthenticationFailed,
    SessionExpired,
    TokenInvalid,
    ConfigInvalid,
    ConfigMissing,
    CommandNotFound,
    UrlInvalid,
    ProcessCrashed,
    ProcessTimeout,
    ToolExecutionError,
    ServerPaused,
    ServerRemoved,
    AlreadyConnected,
    NotConnected,
    OperationCancelled,
    ReconnectExhausted,
    TransitionInvalid
}