using System.Text.Json;
using Microsoft.Extensions.Logging;
using Seeing.Agent.MCP;

namespace Seeing.Agent.Tui.Infrastructure;

/// <summary>
/// 加载 MCP 配置，合并用户级 <c>~/.seeing/mcp.json</c> 与项目级 <c>.seeing/mcp.json</c>。
/// 项目级同名服务覆盖用户级。
/// </summary>
public static class SeeingMcpConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// 加载并合并 MCP Server 配置。
    /// </summary>
    /// <param name="userPath">用户级 mcp.json 路径（可选）</param>
    /// <param name="projectPath">项目级 mcp.json 路径（可选）</param>
    /// <param name="logger">日志记录器（可选，用于记录解析错误）</param>
    /// <returns>合并后的 McpServerConfig 列表</returns>
    public static IReadOnlyList<McpServerConfig> Load(
        string? userPath,
        string? projectPath,
        ILogger? logger = null)
    {
        var configs = new Dictionary<string, McpServerConfig>(StringComparer.OrdinalIgnoreCase);

        // 先加载用户级（作为基础）
        LoadFromFile(userPath, configs, logger, "用户级");

        // 后加载项目级（覆盖同名服务）
        LoadFromFile(projectPath, configs, logger, "项目级");

        return configs.Values.ToList().AsReadOnly();
    }

    /// <summary>
    /// 从单个文件加载配置，合并到字典中。
    /// </summary>
    private static void LoadFromFile(
        string? path,
        Dictionary<string, McpServerConfig> configs,
        ILogger? logger,
        string levelLabel)
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

            var root = doc.RootElement;
            if (!root.TryGetProperty("mcpServers", out var servers) ||
                servers.ValueKind != JsonValueKind.Object)
            {
                logger?.LogWarning("{Level} MCP 配置文件缺少 mcpServers 节: {Path}", levelLabel, path);
                return;
            }

            foreach (var prop in servers.EnumerateObject())
            {
                var name = prop.Name;
                var element = prop.Value;

                if (element.ValueKind != JsonValueKind.Object)
                {
                    logger?.LogWarning("{Level} MCP 服务 '{Name}' 配置格式错误，应为对象: {Path}", levelLabel, name, path);
                    continue;
                }

                try
                {
                    // 直接从 JsonElement 解析为 McpServerConfig
                    var config = ParseServerConfig(name, element);
                    if (config != null && config.IsValid())
                    {
                        configs[name] = config; // 项目级覆盖用户级
                        logger?.LogDebug("加载 {Level} MCP 服务: {Name}", levelLabel, name);
                    }
                    else
                    {
                        logger?.LogWarning("{Level} MCP 服务 '{Name}' 配置无效，已跳过: {Path}", levelLabel, name, path);
                    }
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "{Level} MCP 服务 '{Name}' 解析失败: {Path}", levelLabel, name, path);
                }
            }
        }
        catch (JsonException ex)
        {
            logger?.LogWarning(ex, "{Level} MCP 配置文件 JSON 解析失败: {Path}", levelLabel, path);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "{Level} MCP 配置文件读取失败: {Path}", levelLabel, path);
        }
    }

    /// <summary>
    /// 从 JsonElement 解析单个 MCP Server 配置。
    /// </summary>
    private static McpServerConfig? ParseServerConfig(string name, JsonElement element)
    {
        // 确定传输类型
        var transportType = McpTransportType.Stdio;
        if (element.TryGetProperty("type", out var ttProp))
        {
            var ttStr = ttProp.GetString();
            if (!string.IsNullOrEmpty(ttStr))
            {
                transportType = ttStr.ToLowerInvariant() switch
                {
                    "stdio" => McpTransportType.Stdio,
                    "streamable_http" => McpTransportType.StreamableHttp,
                    "streamableHttp" => McpTransportType.StreamableHttp,
                    "streamablehttp" => McpTransportType.StreamableHttp,
                    "http" => McpTransportType.StreamableHttp,
                    "sse" => McpTransportType.Sse,
                    _ => McpTransportType.Stdio
                };
            }
        }

        var config = new McpServerConfig
        {
            Name = name,
            TransportType = transportType
        };

        // stdio 配置
        if (element.TryGetProperty("command", out var cmdProp) &&
            cmdProp.ValueKind == JsonValueKind.String)
        {
            config.Command = cmdProp.GetString();
        }

        if (element.TryGetProperty("args", out var argsProp) &&
            argsProp.ValueKind == JsonValueKind.Array)
        {
            config.Args = new List<string>();
            foreach (var item in argsProp.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                    config.Args.Add(item.GetString() ?? "");
            }
        }

        if (element.TryGetProperty("env", out var envProp) &&
            envProp.ValueKind == JsonValueKind.Object)
        {
            config.Env = new Dictionary<string, string>();
            foreach (var p in envProp.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String)
                    config.Env[p.Name] = p.Value.GetString() ?? "";
            }
        }

        if (element.TryGetProperty("workingDirectory", out var wdProp) &&
            wdProp.ValueKind == JsonValueKind.String)
        {
            config.WorkingDirectory = wdProp.GetString();
        }

        // HTTP 配置
        if (element.TryGetProperty("url", out var epProp) &&
            epProp.ValueKind == JsonValueKind.String)
        {
            var epStr = epProp.GetString();
            if (!string.IsNullOrEmpty(epStr) && Uri.TryCreate(epStr, UriKind.Absolute, out var uri))
            {
                config.Url = uri;
            }
        }

        if (element.TryGetProperty("headers", out var hdrProp) &&
            hdrProp.ValueKind == JsonValueKind.Object)
        {
            config.Headers = new Dictionary<string, string>();
            foreach (var p in hdrProp.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String)
                    config.Headers[p.Name] = p.Value.GetString() ?? "";
            }
        }

        // 连接管理配置
        if (element.TryGetProperty("connectionTimeout", out var ctProp) &&
            ctProp.ValueKind == JsonValueKind.Number)
        {
            config.ConnectionTimeoutSeconds = ctProp.GetInt32();
        }

        if (element.TryGetProperty("shutdownTimeout", out var stProp) &&
            stProp.ValueKind == JsonValueKind.Number)
        {
            config.ShutdownTimeoutSeconds = stProp.GetInt32();
        }

        if (element.TryGetProperty("maxReconnectionAttempts", out var mraProp) &&
            mraProp.ValueKind == JsonValueKind.Number)
        {
            config.MaxReconnectionAttempts = mraProp.GetInt32();
        }

        if (element.TryGetProperty("reconnectionInterval", out var riProp) &&
            riProp.ValueKind == JsonValueKind.Number)
        {
            config.ReconnectionIntervalMs = riProp.GetInt32();
        }

        return config;
    }

    /// <summary>
    /// 获取默认配置路径（用户级和项目级，仅返回存在的文件）。
    /// </summary>
    public static (string? UserPath, string? ProjectPath) GetDefaultPaths(string workspaceRoot)
    {
        var user = SeeingLayout.UserMcpJsonPath;
        var project = SeeingLayout.ProjectMcpJsonPath(workspaceRoot);
        return (File.Exists(user) ? user : null, File.Exists(project) ? project : null);
    }

    /// <summary>
    /// 加载 MCP 配置的便捷方法，自动获取默认路径。
    /// </summary>
    public static IReadOnlyList<McpServerConfig> LoadDefault(string workspaceRoot, ILogger? logger = null)
    {
        var (userPath, projectPath) = GetDefaultPaths(workspaceRoot);
        return Load(userPath, projectPath, logger);
    }
}