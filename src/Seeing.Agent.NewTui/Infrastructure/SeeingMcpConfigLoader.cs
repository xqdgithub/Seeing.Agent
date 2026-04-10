using System.Text.Json;
using Microsoft.Extensions.Logging;
using Seeing.Agent.MCP;

namespace Seeing.Agent.NewTui.Infrastructure;

/// <summary>
/// 加载 MCP 配置，合并用户级 ~/.seeing/mcp.json 与项目级 .seeing/mcp.json
/// </summary>
public static class SeeingMcpConfigLoader
{
    public static IReadOnlyList<McpServerConfig> LoadDefault(string workspaceRoot, ILogger? logger = null)
    {
        var configs = new Dictionary<string, McpServerConfig>(StringComparer.OrdinalIgnoreCase);

        var userPath = SeeingLayout.UserMcpJsonPath;
        var projectPath = SeeingLayout.ProjectMcpJsonPath(workspaceRoot);

        LoadFromFile(userPath, configs, logger, "用户级");
        LoadFromFile(projectPath, configs, logger, "项目级");

        return configs.Values.ToList().AsReadOnly();
    }

    private static void LoadFromFile(string? path, Dictionary<string, McpServerConfig> configs, ILogger? logger, string levelLabel)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return;

        try
        {
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });

            if (!doc.RootElement.TryGetProperty("mcpServers", out var servers) ||
                servers.ValueKind != JsonValueKind.Object)
                return;

            foreach (var prop in servers.EnumerateObject())
            {
                var config = ParseServerConfig(prop.Name, prop.Value);
                if (config?.IsValid() == true)
                    configs[prop.Name] = config;
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "{Level} MCP 配置文件读取失败: {Path}", levelLabel, path);
        }
    }

    private static McpServerConfig? ParseServerConfig(string name, JsonElement element)
    {
        var transportType = McpTransportType.Stdio;
        if (element.TryGetProperty("type", out var ttProp))
        {
            var ttStr = ttProp.GetString();
            transportType = ttStr?.ToLowerInvariant() switch
            {
                "stdio" => McpTransportType.Stdio,
                "http" or "streamable_http" => McpTransportType.StreamableHttp,
                "sse" => McpTransportType.Sse,
                _ => McpTransportType.Stdio
            };
        }

        var config = new McpServerConfig { Name = name, TransportType = transportType };

        if (element.TryGetProperty("command", out var cmdProp))
            config.Command = cmdProp.GetString();

        if (element.TryGetProperty("args", out var argsProp) && argsProp.ValueKind == JsonValueKind.Array)
        {
            config.Args = new List<string>();
            foreach (var item in argsProp.EnumerateArray())
                config.Args.Add(item.GetString() ?? "");
        }

        if (element.TryGetProperty("env", out var envProp) && envProp.ValueKind == JsonValueKind.Object)
        {
            config.Env = new Dictionary<string, string>();
            foreach (var p in envProp.EnumerateObject())
                config.Env[p.Name] = p.Value.GetString() ?? "";
        }

        if (element.TryGetProperty("url", out var urlProp))
        {
            var urlStr = urlProp.GetString();
            if (!string.IsNullOrEmpty(urlStr) && Uri.TryCreate(urlStr, UriKind.Absolute, out var uri))
                config.Url = uri;
        }

        return config;
    }
}