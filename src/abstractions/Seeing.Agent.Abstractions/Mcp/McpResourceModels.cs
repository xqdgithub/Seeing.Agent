namespace Seeing.Agent.Abstractions.Mcp;

/// <summary>MCP resources/list 条目。</summary>
public sealed record McpResourceInfo(
    string Uri,
    string Name,
    string? Description = null,
    string? MimeType = null);

/// <summary>MCP resources/templates/list 条目。</summary>
public sealed record McpResourceTemplateInfo(
    string UriTemplate,
    string Name,
    string? Description = null,
    string? MimeType = null);

/// <summary>MCP resources/read 返回的内容块。</summary>
public sealed record McpResourceContent(
    string Uri,
    string? MimeType = null,
    string? Text = null,
    string? BlobBase64 = null);

/// <summary>resources/list 结果。无 capability 时 <see cref="NotSupported"/> 为 true。</summary>
public sealed record McpResourceListResult(
    IReadOnlyList<McpResourceInfo> Items,
    string? NextCursor = null,
    bool NotSupported = false,
    string? ErrorMessage = null)
{
    public static McpResourceListResult Unsupported(string? message = null)
        => new(Array.Empty<McpResourceInfo>(), NotSupported: true, ErrorMessage: message);
}

/// <summary>resources/read 结果。无 capability 时 <see cref="NotSupported"/> 为 true。</summary>
public sealed record McpResourceReadResult(
    IReadOnlyList<McpResourceContent> Contents,
    bool NotSupported = false,
    string? ErrorMessage = null)
{
    public static McpResourceReadResult Unsupported(string? message = null)
        => new(Array.Empty<McpResourceContent>(), NotSupported: true, ErrorMessage: message);
}

/// <summary>resources/templates/list 结果。无 capability 时 <see cref="NotSupported"/> 为 true。</summary>
public sealed record McpResourceTemplateListResult(
    IReadOnlyList<McpResourceTemplateInfo> Items,
    string? NextCursor = null,
    bool NotSupported = false,
    string? ErrorMessage = null)
{
    public static McpResourceTemplateListResult Unsupported(string? message = null)
        => new(Array.Empty<McpResourceTemplateInfo>(), NotSupported: true, ErrorMessage: message);
}
