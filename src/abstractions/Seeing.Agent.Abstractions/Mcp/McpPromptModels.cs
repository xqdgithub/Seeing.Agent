namespace Seeing.Agent.Abstractions.Mcp;

/// <summary>MCP prompts/list 参数描述。</summary>
public sealed record McpPromptArgumentInfo(
    string Name,
    string? Description = null,
    bool Required = false);

/// <summary>MCP prompts/list 条目。</summary>
public sealed record McpPromptInfo(
    string Name,
    string? Description = null,
    IReadOnlyList<McpPromptArgumentInfo>? Arguments = null);

/// <summary>MCP prompts/get 返回的消息段。</summary>
public sealed record McpPromptMessage(
    string Role,
    string Text);

/// <summary>prompts/list 结果。无 capability 时 <see cref="NotSupported"/> 为 true。</summary>
public sealed record McpPromptListResult(
    IReadOnlyList<McpPromptInfo> Items,
    string? NextCursor = null,
    bool NotSupported = false,
    string? ErrorMessage = null)
{
    public static McpPromptListResult Unsupported(string? message = null)
        => new(Array.Empty<McpPromptInfo>(), NotSupported: true, ErrorMessage: message);
}

/// <summary>prompts/get 结果。无 capability 时 <see cref="NotSupported"/> 为 true。</summary>
public sealed record McpPromptGetResult(
    IReadOnlyList<McpPromptMessage> Messages,
    string? Description = null,
    bool NotSupported = false,
    string? ErrorMessage = null)
{
    public static McpPromptGetResult Unsupported(string? message = null)
        => new(Array.Empty<McpPromptMessage>(), NotSupported: true, ErrorMessage: message);
}
