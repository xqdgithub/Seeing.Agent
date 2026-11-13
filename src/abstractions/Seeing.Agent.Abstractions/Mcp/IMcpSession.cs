namespace Seeing.Agent.Abstractions.Mcp;

/// <summary>
/// 单个已连接 MCP server 的协议会话面。
/// 按 capabilities 门控：无对应能力时方法返回 NotSupported，禁止把未处理 SDK 异常冒充成功。
/// </summary>
public interface IMcpSession : IAsyncDisposable
{
    string ServerName { get; }
    McpServerCapabilitiesInfo Capabilities { get; }

    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<McpSessionToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default);
    Task<McpSessionToolCallResult> CallToolAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default);

    Task<McpResourceListResult> ListResourcesAsync(
        string? cursor = null,
        CancellationToken cancellationToken = default);
    Task<McpResourceReadResult> ReadResourceAsync(
        string uri,
        CancellationToken cancellationToken = default);
    Task<McpResourceTemplateListResult> ListResourceTemplatesAsync(
        string? cursor = null,
        CancellationToken cancellationToken = default);

    Task<McpPromptListResult> ListPromptsAsync(
        string? cursor = null,
        CancellationToken cancellationToken = default);
    Task<McpPromptGetResult> GetPromptAsync(
        string name,
        IReadOnlyDictionary<string, object?>? arguments = null,
        CancellationToken cancellationToken = default);

    Task<McpSkillExtListResult> ListSkillsAsync(
        string? cursor = null,
        CancellationToken cancellationToken = default);
    Task<McpSkillExtGetResult> GetSkillAsync(
        string uri,
        CancellationToken cancellationToken = default);
}

/// <summary>会话面 tools/list 条目（与状态投影用的 <see cref="McpToolInfo"/> 分离）。</summary>
public sealed record McpSessionToolInfo(
    string Name,
    string? Description = null,
    string? InputSchemaJson = null);

/// <summary>会话面 tools/call 结果。</summary>
public sealed record McpSessionToolCallResult(
    bool IsError,
    string Content,
    string? ErrorMessage = null);
