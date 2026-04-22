using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Seeing.Agent.MCP;

/// <summary>
/// MCP 配置加载器 - 从 mcp.json 加载并合并用户级和项目级配置
/// <para>
/// 配置文件位置：
/// - 用户级：~/.seeing/mcp.json
/// - 项目级：./.seeing/mcp.json
/// 项目级同名服务覆盖用户级
/// </para>
/// </summary>
public static class McpConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>用户级 ~/.seeing 目录</summary>
    private static string UserSeeingDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".seeing");

    /// <summary>用户级 mcp.json 路径</summary>
    public static string UserMcpJsonPath => Path.Combine(UserSeeingDirectory, "mcp.json");

    /// <summary>项目级 mcp.json 路径</summary>
    public static string ProjectMcpJsonPath(string workspaceRoot) =>
        Path.Combine(workspaceRoot, ".seeing", "mcp.json");

    /// <summary>
    /// 加载并合并 MCP Server 配置
    /// </summary>
    public static IReadOnlyList<McpServerConfig> Load(string? userPath, string? projectPath, ILogger? logger = null)
    {
        var configs = new Dictionary<string, McpServerConfig>(StringComparer.OrdinalIgnoreCase);

        // 先加载用户级（作为基础）
        LoadFromFile(userPath, configs, logger, "用户级");

        // 后加载项目级（覆盖同名服务）
        LoadFromFile(projectPath, configs, logger, "项目级");

        return configs.Values.ToList().AsReadOnly();
    }

    /// <summary>
    /// 加载默认路径的 MCP 配置
    /// </summary>
    public static IReadOnlyList<McpServerConfig> LoadDefault(string workspaceRoot, ILogger? logger = null)
    {
        var userPath = File.Exists(UserMcpJsonPath) ? UserMcpJsonPath : null;
        var projectPath = File.Exists(ProjectMcpJsonPath(workspaceRoot)) ? ProjectMcpJsonPath(workspaceRoot) : null;
        return Load(userPath, projectPath, logger);
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
            {
                logger?.LogWarning("{Level} MCP 配置文件缺少 mcpServers 节: {Path}", levelLabel, path);
                return;
            }

            foreach (var prop in servers.EnumerateObject())
            {
                var config = ParseServerConfig(prop.Name, prop.Value);
                if (config != null && config.IsValid())
                {
                    configs[prop.Name] = config;
                    logger?.LogDebug("加载 {Level} MCP 服务: {Name}", levelLabel, prop.Name);
                }
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "{Level} MCP 配置文件加载失败: {Path}", levelLabel, path);
        }
    }

    private static McpServerConfig? ParseServerConfig(string name, JsonElement element)
    {
        var transportType = element.TryGetProperty("type", out var ttProp)
            ? ParseTransportType(ttProp.GetString())
            : McpTransportType.Stdio;

        var config = new McpServerConfig { Name = name, TransportType = transportType };

        // stdio 配置
        if (element.TryGetProperty("command", out var cmdProp))
            config.Command = cmdProp.GetString();

        if (element.TryGetProperty("args", out var argsProp) && argsProp.ValueKind == JsonValueKind.Array)
        {
            config.Args = argsProp.EnumerateArray().Select(a => a.GetString() ?? "").ToList();
        }

        if (element.TryGetProperty("env", out var envProp) && envProp.ValueKind == JsonValueKind.Object)
        {
            config.Env = envProp.EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");
        }

        // HTTP 配置
        if (element.TryGetProperty("url", out var urlProp))
        {
            var urlStr = urlProp.GetString();
            if (!string.IsNullOrEmpty(urlStr) && Uri.TryCreate(urlStr, UriKind.Absolute, out var uri))
                config.Url = uri;
        }

        if (element.TryGetProperty("headers", out var hdrProp) && hdrProp.ValueKind == JsonValueKind.Object)
        {
            config.Headers = hdrProp.EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");
        }

        return config;
    }

    private static McpTransportType ParseTransportType(string? type)
    {
        return type?.ToLowerInvariant() switch
        {
            "stdio" => McpTransportType.Stdio,
            "streamable_http" or "streamablehttp" or "http" => McpTransportType.StreamableHttp,
            "sse" => McpTransportType.Sse,
            _ => McpTransportType.Stdio
        };
    }
}