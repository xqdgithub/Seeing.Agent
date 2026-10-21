namespace Seeing.Agent.Mcp.Management;

/// <summary>
/// MCP 同名 server 重复注册异常：同一 server 名已被另一个连接占用。
/// <para>同一连接的重连（同连接标识、同名同工具）不触发本异常。</para>
/// </summary>
public sealed class McpDuplicateServerException : InvalidOperationException
{
    /// <summary>重复的 server 名。</summary>
    public string ServerName { get; }

    /// <summary>以 server 名创建异常。</summary>
    public McpDuplicateServerException(string serverName)
        : base($"MCP Server '{serverName}' 重复注册：该名称已被另一个连接占用，请先断开或移除原连接。")
    {
        ServerName = serverName;
    }
}
