using System.Text;
using ModelContextProtocol.Protocol;
using Seeing.Agent.Abstractions.Mcp;

namespace Seeing.Agent.Mcp.Session;

/// <summary>将 SDK tools/call 内容块格式化为会话面文本（含 EmbeddedResource 跟进提示）。</summary>
public static class McpSessionToolResultBuilder
{
    public static McpSessionToolCallResult FromCallToolResult(CallToolResult result)
    {
        var contentBuilder = new StringBuilder();
        var hasError = result.IsError ?? false;

        foreach (var content in result.Content)
        {
            if (content is TextContentBlock textBlock)
                contentBuilder.AppendLine(textBlock.Text);
            else if (content is ImageContentBlock imageBlock)
                contentBuilder.AppendLine($"[Image: {imageBlock.MimeType}]");
            else if (content is EmbeddedResourceBlock resourceBlock)
                contentBuilder.AppendLine(FormatEmbeddedResource(resourceBlock.Resource?.Uri));
            else
                contentBuilder.AppendLine($"[Content: {content.Type}]");
        }

        return new McpSessionToolCallResult(
            IsError: hasError,
            Content: contentBuilder.ToString().TrimEnd());
    }

    public static string FormatEmbeddedResource(string? uri)
    {
        var displayUri = string.IsNullOrWhiteSpace(uri) ? "(unknown)" : uri;
        return $"[Resource: {displayUri}] 可用 mcp_read_resource 按需读取该 URI";
    }
}
