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
    TransitionInvalid,

    /// <summary>配置持久化失败</summary>
    PersistenceError,

    /// <summary>JSON 解析失败</summary>
    JsonParseError,

    /// <summary>配置导入失败</summary>
    ImportError
}