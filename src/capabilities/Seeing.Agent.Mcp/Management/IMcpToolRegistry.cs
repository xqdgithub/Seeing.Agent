namespace Seeing.Agent.Mcp.Management;

using Seeing.Agent.Abstractions.Mcp;
using Seeing.Agent.Mcp;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// MCP 工具注册表：维护 server 名占用、工具 id 映射与工具执行器绑定。
/// </summary>
public interface IMcpToolRegistry
{
    /// <summary>
    /// 注册一个 MCP 工具。
    /// <para>
    /// <paramref name="owner"/> 为发起注册的连接标识（连接协调器实例）。同名 server 已由
    /// 另一个连接占用时抛出 <see cref="McpDuplicateServerException"/>；同一连接的重连不视为冲突。
    /// </para>
    /// </summary>
    Task RegisterToolAsync(string serverName, string toolId, McpToolInfo toolInfo, object owner, CancellationToken ct = default);

    /// <summary>
    /// 占用 server 名（注册工具之前调用，使零工具 server 也能拒绝同名抢占）。
    /// 同一 <paramref name="owner"/> 重复占用视为重连，不报错。
    /// </summary>
    /// <exception cref="McpDuplicateServerException">同名 server 已被另一连接占用。</exception>
    void ClaimServer(string serverName, object owner);

    /// <summary>
    /// 释放 server 名占用；仅当 <paramref name="owner"/> 是当前占用者且该 server 已无注册工具时生效。
    /// 供连接失败/回滚路径调用，避免失败连接长期占名。
    /// </summary>
    void ReleaseServer(string serverName, object owner);

    /// <summary>注销单个工具（最后一个工具移除时释放 server 名占用）。</summary>
    Task UnregisterToolAsync(string serverName, string toolId, CancellationToken ct = default);

    /// <summary>注销该 server 的全部工具，并释放其 server 名占用。</summary>
    Task<McpOperationResult> UnregisterAllToolsAsync(string serverName);

    Task UpdateToolExecutorAsync(string serverName, Func<string, Dictionary<string, object?>, CancellationToken, Task<McpToolResult>> executor);
    bool HasTool(string toolId);
    int GetToolCount(string serverName);
}
