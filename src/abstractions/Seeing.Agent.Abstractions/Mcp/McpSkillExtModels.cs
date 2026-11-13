namespace Seeing.Agent.Abstractions.Mcp;

/// <summary>
/// MCP Skills 扩展（io.modelcontextprotocol/skills）list 条目。
/// 发现期只带元数据；正文经 get / resources/read 回源。
/// </summary>
public sealed record McpSkillExtInfo(
    string Uri,
    string Name,
    string? Description = null,
    string? FrontmatterJson = null);

/// <summary>skills/list 结果。未声明扩展时 <see cref="NotSupported"/> 为 true。</summary>
public sealed record McpSkillExtListResult(
    IReadOnlyList<McpSkillExtInfo> Items,
    string? NextCursor = null,
    bool NotSupported = false,
    string? ErrorMessage = null)
{
    public static McpSkillExtListResult Unsupported(string? message = null)
        => new(Array.Empty<McpSkillExtInfo>(), NotSupported: true, ErrorMessage: message);
}

/// <summary>skills/get 结果。未声明扩展时 <see cref="NotSupported"/> 为 true。</summary>
public sealed record McpSkillExtGetResult(
    string? Uri = null,
    string? Name = null,
    string? Description = null,
    string? Content = null,
    string? FrontmatterJson = null,
    IReadOnlyList<string>? AttachmentUris = null,
    bool NotSupported = false,
    string? ErrorMessage = null)
{
    public static McpSkillExtGetResult Unsupported(string? message = null)
        => new(NotSupported: true, ErrorMessage: message);
}
