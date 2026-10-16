using Seeing.Agent.Abstractions.Interactions;
using System.Text.Json;

namespace Seeing.Agent.Tools.Editor;

/// <summary>编辑器 Host Request 载荷构造（schema 单一来源）。</summary>
public static class EditorHostRequests
{
    /// <summary>执行编辑器命令的请求类型。</summary>
    public const string CommandKind = "editor.command";

    /// <summary>查询编辑器状态的请求类型。</summary>
    public const string QueryKind = "editor.query";

    /// <summary>枚举编辑器可用命令的请求类型。</summary>
    public const string ListCommandsKind = "editor.list_commands";

    /// <summary>构造「执行命令」请求。</summary>
    public static HostRequest Command(
        string sessionId, string? callId, string commandId, JsonElement? args, string? path)
        => new()
        {
            SessionId = sessionId,
            CallId = callId,
            Kind = CommandKind,
            Payload = JsonSerializer.SerializeToElement(new { commandId, args, path })
        };

    /// <summary>构造「查询状态」请求。</summary>
    public static HostRequest Query(string sessionId, string? callId, string queryKind, string? path)
        => new()
        {
            SessionId = sessionId,
            CallId = callId,
            Kind = QueryKind,
            Payload = JsonSerializer.SerializeToElement(new { kind = queryKind, path })
        };

    /// <summary>构造「枚举命令」请求。</summary>
    public static HostRequest ListCommands(string sessionId, string? callId)
        => new()
        {
            SessionId = sessionId,
            CallId = callId,
            Kind = ListCommandsKind
        };
}
