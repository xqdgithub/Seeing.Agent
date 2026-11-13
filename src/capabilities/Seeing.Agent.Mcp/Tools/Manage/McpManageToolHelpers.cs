using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Mcp;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Tools.Support;
using System.Text;
using System.Text.Json;

namespace Seeing.Agent.Mcp.Tools.Manage;

/// <summary>管理工具共用：参数解析、权限门、结果格式化。</summary>
internal static class McpManageToolHelpers
{
    internal static async Task<ToolResult?> AuthorizeOrDenyAsync(
        ToolBase tool,
        string permissionKind,
        string resource,
        JsonElement arguments,
        ToolContext context)
    {
        var gate = await McpManagePermissionGate
            .AuthorizeAsync(permissionKind, resource, tool.Id, arguments, context)
            .ConfigureAwait(false);

        if (gate is { Decision: not PermissionEffect.Allow })
        {
            return new ToolResult
            {
                Success = false,
                Title = "MCP 管理授权未通过",
                Output = gate.Reason ?? "权限被拒绝",
                Error = gate.Reason ?? "Permission denied"
            };
        }

        return null;
    }

    internal static string? RequireName(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("name", out var prop) || prop.ValueKind != JsonValueKind.String)
            return null;
        var name = prop.GetString();
        return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    internal static bool TryParseLevel(JsonElement arguments, out ConfigLevel level, out string? error)
    {
        level = default;
        error = null;
        if (!arguments.TryGetProperty("level", out var prop) || prop.ValueKind != JsonValueKind.String)
        {
            error = "缺少必填参数 level（User|Project）。";
            return false;
        }

        var raw = prop.GetString();
        if (string.Equals(raw, "User", StringComparison.OrdinalIgnoreCase))
        {
            level = ConfigLevel.User;
            return true;
        }

        if (string.Equals(raw, "Project", StringComparison.OrdinalIgnoreCase))
        {
            level = ConfigLevel.Project;
            return true;
        }

        error = "level 必须是 User 或 Project。";
        return false;
    }

    /// <summary>解析 connect 三态：缺省 → null（跟随 AutoStartOnAdd）；true/false 显式覆盖。</summary>
    internal static bool? ParseConnect(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("connect", out var prop))
            return null;
        return prop.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    internal static McpServerConfig? ParseServerConfig(JsonElement arguments, out string? error)
    {
        error = null;
        var config = new McpServerConfig();

        if (arguments.TryGetProperty("transportType", out var transportProp) &&
            transportProp.ValueKind == JsonValueKind.String)
        {
            var t = transportProp.GetString();
            if (string.Equals(t, "stdio", StringComparison.OrdinalIgnoreCase))
                config.TransportType = McpTransportType.Stdio;
            else if (string.Equals(t, "streamableHttp", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(t, "http", StringComparison.OrdinalIgnoreCase))
                config.TransportType = McpTransportType.StreamableHttp;
            else if (string.Equals(t, "sse", StringComparison.OrdinalIgnoreCase))
                config.TransportType = McpTransportType.Sse;
            else
            {
                error = "transportType 必须是 stdio、streamableHttp 或 sse。";
                return null;
            }
        }

        if (arguments.TryGetProperty("command", out var cmd) && cmd.ValueKind == JsonValueKind.String)
            config.Command = cmd.GetString();
        if (arguments.TryGetProperty("workingDirectory", out var wd) && wd.ValueKind == JsonValueKind.String)
            config.WorkingDirectory = wd.GetString();
        if (arguments.TryGetProperty("description", out var desc) && desc.ValueKind == JsonValueKind.String)
            config.Description = desc.GetString();
        if (arguments.TryGetProperty("disabled", out var disabled) &&
            (disabled.ValueKind is JsonValueKind.True or JsonValueKind.False))
            config.Disabled = disabled.GetBoolean();

        if (arguments.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array)
        {
            config.Args = new List<string>();
            foreach (var item in args.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s)
                    config.Args.Add(s);
            }
        }

        if (arguments.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Object)
        {
            config.Env = new Dictionary<string, string>();
            foreach (var prop in env.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String && prop.Value.GetString() is { } v)
                    config.Env[prop.Name] = v;
            }
        }

        if (arguments.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
        {
            var raw = url.GetString();
            if (!string.IsNullOrWhiteSpace(raw) && Uri.TryCreate(raw, UriKind.Absolute, out var uri))
                config.Url = uri;
            else if (!string.IsNullOrWhiteSpace(raw))
            {
                error = "url 必须是绝对 URI。";
                return null;
            }
        }

        if (arguments.TryGetProperty("headers", out var headers) && headers.ValueKind == JsonValueKind.Object)
        {
            config.Headers = new Dictionary<string, string>();
            foreach (var prop in headers.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String && prop.Value.GetString() is { } v)
                    config.Headers[prop.Name] = v;
            }
        }

        if (!config.IsValid())
        {
            error = config.TransportType == McpTransportType.Stdio
                ? "stdio 传输需要 command。"
                : "HTTP/SSE 传输需要 url。";
            return null;
        }

        return config;
    }

    internal static string FormatOperation(McpOperationResult result)
    {
        if (!result.Success)
            return result.Error?.UserMessage ?? "操作失败";

        var sb = new StringBuilder();
        sb.Append(result.ServerName).Append(": ").Append(result.OperationType);
        if (result.Status is { } state)
            sb.Append(" → ").Append(state);
        return sb.ToString();
    }

    internal static string FormatStatus(McpServerStatus status)
    {
        var sb = new StringBuilder();
        sb.Append("- ").Append(status.Name)
            .Append(" | state=").Append(status.State)
            .Append(" | tools=").Append(status.ToolCount)
            .Append(" | level=").Append(status.Config?.ConfigLevel?.ToString() ?? "?");

        if (status.Capabilities is { } caps)
        {
            sb.Append(" | caps: tools=").Append(caps.Tools.ToString().ToLowerInvariant())
                .Append(" resources=").Append(caps.Resources.ToString().ToLowerInvariant())
                .Append(" prompts=").Append(caps.Prompts.ToString().ToLowerInvariant())
                .Append(" skills=").Append(caps.SkillsExtension.ToString().ToLowerInvariant());
        }

        if (status.ResourceCount is { } rc)
            sb.Append(" | resourceCount=").Append(rc);
        if (status.PromptCount is { } pc)
            sb.Append(" | promptCount=").Append(pc);
        if (status.SkillCount is { } sc)
            sb.Append(" | skillCount=").Append(sc);

        if (status.ToolNames.Count > 0)
            sb.Append(" | toolNames=[").Append(string.Join(", ", status.ToolNames)).Append(']');

        return sb.ToString();
    }
}
