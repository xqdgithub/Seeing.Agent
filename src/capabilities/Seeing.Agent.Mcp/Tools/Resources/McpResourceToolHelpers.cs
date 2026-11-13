using Seeing.Agent.Abstractions.Mcp;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Tools;
using System.Text;
using System.Text.Json;

namespace Seeing.Agent.Mcp.Tools.Resources;

/// <summary>Resource 工具共用：取会话、权限门、capabilities 门控、内容格式化。</summary>
internal static class McpResourceToolHelpers
{
    internal const string NotSupportedMessage =
        "该 MCP server 未声明 resources capability，无法列出或读取资源。请改用其它 server，或确认远端已启用 Resources。";

    internal static string? RequireServer(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("server", out var prop) || prop.ValueKind != JsonValueKind.String)
            return null;
        var server = prop.GetString();
        return string.IsNullOrWhiteSpace(server) ? null : server.Trim();
    }

    internal static string? OptionalCursor(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("cursor", out var prop) || prop.ValueKind != JsonValueKind.String)
            return null;
        var cursor = prop.GetString();
        return string.IsNullOrWhiteSpace(cursor) ? null : cursor;
    }

    internal static string? RequireUri(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("uri", out var prop) || prop.ValueKind != JsonValueKind.String)
            return null;
        var uri = prop.GetString();
        return string.IsNullOrWhiteSpace(uri) ? null : uri.Trim();
    }

    internal static async Task<(IMcpSession? Session, ToolResult? Error)> AuthorizeAndResolveSessionAsync(
        IMcpManager manager,
        string serverName,
        string? uriForPermission,
        JsonElement arguments,
        ToolContext context)
    {
        var gate = await McpResourcePermissionGate
            .AuthorizeAsync(serverName, uriForPermission, arguments, context)
            .ConfigureAwait(false);

        if (gate is { Decision: not PermissionEffect.Allow })
        {
            return (null, new ToolResult
            {
                Success = false,
                Title = "MCP 资源授权未通过",
                Output = gate.Reason ?? "权限被拒绝",
                Error = gate.Reason ?? "Permission denied"
            });
        }

        if (!manager.IsAvailable(serverName))
        {
            return (null, new ToolResult
            {
                Success = false,
                Title = "MCP Server 不可用",
                Error = $"MCP Server 未连接或不可用: {serverName}"
            });
        }

        var session = manager.TryGetSession(serverName);
        if (session is null)
        {
            return (null, new ToolResult
            {
                Success = false,
                Title = "MCP 会话缺失",
                Error = $"MCP Server 会话未初始化: {serverName}"
            });
        }

        if (!session.Capabilities.Resources)
        {
            return (null, new ToolResult
            {
                Success = false,
                Title = "不支持 Resources",
                Error = NotSupportedMessage
            });
        }

        return (session, null);
    }

    internal static string FormatResourceList(IReadOnlyList<McpResourceInfo> items, string? nextCursor)
    {
        if (items.Count == 0)
            return nextCursor is null
                ? "无资源条目。"
                : $"无资源条目。nextCursor={nextCursor}";

        var sb = new StringBuilder();
        sb.AppendLine($"共 {items.Count} 个资源：");
        foreach (var item in items)
        {
            sb.Append("- ").Append(item.Uri);
            if (!string.IsNullOrEmpty(item.Name))
                sb.Append(" (").Append(item.Name).Append(')');
            if (!string.IsNullOrEmpty(item.MimeType))
                sb.Append(" [").Append(item.MimeType).Append(']');
            if (!string.IsNullOrEmpty(item.Description))
                sb.Append(" — ").Append(item.Description);
            sb.AppendLine();
        }

        if (!string.IsNullOrEmpty(nextCursor))
            sb.AppendLine($"nextCursor={nextCursor}");

        return sb.ToString().TrimEnd();
    }

    internal static string FormatTemplateList(IReadOnlyList<McpResourceTemplateInfo> items, string? nextCursor)
    {
        if (items.Count == 0)
            return nextCursor is null
                ? "无资源模板。"
                : $"无资源模板。nextCursor={nextCursor}";

        var sb = new StringBuilder();
        sb.AppendLine($"共 {items.Count} 个资源模板：");
        foreach (var item in items)
        {
            sb.Append("- ").Append(item.UriTemplate);
            if (!string.IsNullOrEmpty(item.Name))
                sb.Append(" (").Append(item.Name).Append(')');
            if (!string.IsNullOrEmpty(item.MimeType))
                sb.Append(" [").Append(item.MimeType).Append(']');
            if (!string.IsNullOrEmpty(item.Description))
                sb.Append(" — ").Append(item.Description);
            sb.AppendLine();
        }

        if (!string.IsNullOrEmpty(nextCursor))
            sb.AppendLine($"nextCursor={nextCursor}");

        return sb.ToString().TrimEnd();
    }

    internal static string FormatReadContents(IReadOnlyList<McpResourceContent> contents)
    {
        if (contents.Count == 0)
            return "资源内容为空。";

        var sb = new StringBuilder();
        for (var i = 0; i < contents.Count; i++)
        {
            var c = contents[i];
            if (contents.Count > 1)
                sb.AppendLine($"--- 内容块 {i + 1} ---");

            sb.Append("uri: ").AppendLine(c.Uri);
            if (!string.IsNullOrEmpty(c.MimeType))
                sb.Append("mime: ").AppendLine(c.MimeType);

            if (!string.IsNullOrEmpty(c.Text))
            {
                sb.AppendLine(c.Text);
            }
            else if (!string.IsNullOrEmpty(c.BlobBase64))
            {
                var approxBytes = EstimateBase64ByteCount(c.BlobBase64);
                sb.Append("[binary 摘要] mime=")
                    .Append(string.IsNullOrEmpty(c.MimeType) ? "unknown" : c.MimeType)
                    .Append("，约 ")
                    .Append(approxBytes)
                    .AppendLine(" 字节（已省略 base64，避免撑爆上下文）");
            }
            else
            {
                sb.AppendLine("(无文本或二进制载荷)");
            }

            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    internal static int EstimateBase64ByteCount(string base64)
    {
        var len = base64.AsSpan().TrimEnd('=').Length;
        return (int)(len * 3L / 4);
    }
}
