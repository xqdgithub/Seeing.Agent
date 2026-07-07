using Microsoft.Extensions.Logging;
using Seeing.Agent.Configuration;
using Seeing.Agent.MCP.Core;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Seeing.Agent.MCP.Configuration;

/// <summary>
/// MCP 配置持久化实现 - 负责配置文件的读取、写入和序列化
/// </summary>
public class McpConfigPersistence : IMcpConfigPersistence
{
    private readonly ILogger<McpConfigPersistence> _logger;
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly JsonSerializerOptions _jsonOptions;

    public McpConfigPersistence(
        ILogger<McpConfigPersistence> logger,
        IWorkspaceProvider workspaceProvider)
    {
        _logger = logger;
        _workspaceProvider = workspaceProvider;

        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };
    }

    /// <summary>
    /// 获取指定级别配置文件的路径
    /// </summary>
    public string GetConfigPath(ConfigLevel level)
    {
        return level switch
        {
            ConfigLevel.User => Path.Combine(_workspaceProvider.UserSeeingDirectory, "mcp.json"),
            ConfigLevel.Project => Path.Combine(_workspaceProvider.ProjectSeeingDirectory, "mcp.json"),
            _ => throw new ArgumentOutOfRangeException(nameof(level))
        };
    }

    /// <summary>
    /// 检查指定级别的配置文件是否存在
    /// </summary>
    public bool ConfigExists(ConfigLevel level) => File.Exists(GetConfigPath(level));

    /// <summary>
    /// 加载指定级别的配置
    /// </summary>
    public async Task<IReadOnlyDictionary<string, McpServerConfig>> LoadAsync(
        ConfigLevel level, CancellationToken cancellationToken = default)
    {
        var path = GetConfigPath(level);
        if (!File.Exists(path))
            return new Dictionary<string, McpServerConfig>();

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });

            if (!doc.RootElement.TryGetProperty("mcpServers", out var servers))
                return new Dictionary<string, McpServerConfig>();

            var configs = new Dictionary<string, McpServerConfig>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in servers.EnumerateObject())
            {
                var config = ParseServerConfig(prop.Name, prop.Value);
                if (config != null && config.IsValid())
                {
                    config.ConfigLevel = level;
                    configs[prop.Name] = config;
                }
            }

            _logger.LogDebug("从 {Path} 加载了 {Count} 个 MCP 配置", path, configs.Count);
            return configs;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载 MCP 配置失败: {Path}", path);
            return new Dictionary<string, McpServerConfig>();
        }
    }

    /// <summary>
    /// 保存指定级别的配置
    /// </summary>
    public async Task SaveAsync(
        ConfigLevel level,
        IReadOnlyDictionary<string, McpServerConfig> configs,
        CancellationToken cancellationToken = default)
    {
        var path = GetConfigPath(level);
        var directory = Path.GetDirectoryName(path)!;

        if (!Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        var output = new Dictionary<string, object>
        {
            ["mcpServers"] = configs.ToDictionary(
                kvp => kvp.Key,
                kvp => SerializeServerConfigAsObject(kvp.Value))
        };

        var json = JsonSerializer.Serialize(output, _jsonOptions);
        await File.WriteAllTextAsync(path, json, cancellationToken);

        _logger.LogInformation("已保存 {Count} 个 MCP 配置到: {Path}", configs.Count, path);
    }

    /// <summary>
    /// 解析单个服务器配置
    /// </summary>
    public McpServerConfig? ParseServerConfig(string name, JsonElement element)
    {
        try
        {
            // 解析传输类型
            var transportType = element.TryGetProperty("type", out var ttProp)
                ? ParseTransportType(ttProp.GetString())
                : McpTransportType.Stdio;

            var config = new McpServerConfig
            {
                Name = name,
                TransportType = transportType
            };

            // stdio 配置
            if (element.TryGetProperty("command", out var cmdProp))
                config.Command = cmdProp.GetString();

            if (element.TryGetProperty("args", out var argsProp) && argsProp.ValueKind == JsonValueKind.Array)
                config.Args = argsProp.EnumerateArray().Select(a => a.GetString() ?? "").ToList();

            if (element.TryGetProperty("env", out var envProp) && envProp.ValueKind == JsonValueKind.Object)
                config.Env = envProp.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");

            if (element.TryGetProperty("workingDirectory", out var wdProp))
                config.WorkingDirectory = wdProp.GetString();

            // HTTP 配置
            if (element.TryGetProperty("url", out var urlProp))
            {
                var urlStr = urlProp.GetString();
                if (!string.IsNullOrEmpty(urlStr) && Uri.TryCreate(urlStr, UriKind.Absolute, out var uri))
                    config.Url = uri;
            }

            if (element.TryGetProperty("headers", out var hdrProp) && hdrProp.ValueKind == JsonValueKind.Object)
                config.Headers = hdrProp.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");

            // 禁用状态
            if (element.TryGetProperty("disabled", out var disabledProp))
                config.Disabled = disabledProp.GetBoolean();

            // 其他配置字段
            if (element.TryGetProperty("description", out var descProp))
                config.Description = descProp.GetString();

            if (element.TryGetProperty("tags", out var tagsProp) && tagsProp.ValueKind == JsonValueKind.Array)
                config.Tags = tagsProp.EnumerateArray().Select(a => a.GetString() ?? "").ToList();

            // 连接配置
            if (element.TryGetProperty("connectionTimeout", out var timeoutProp))
                config.ConnectionTimeoutSeconds = timeoutProp.GetInt32();

            if (element.TryGetProperty("shutdownTimeout", out var shutdownProp))
                config.ShutdownTimeoutSeconds = shutdownProp.GetInt32();

            if (element.TryGetProperty("maxReconnectionAttempts", out var maxReconnProp))
                config.MaxReconnectionAttempts = maxReconnProp.GetInt32();

            if (element.TryGetProperty("reconnectionInterval", out var reconnIntervalProp))
                config.ReconnectionIntervalMs = reconnIntervalProp.GetInt32();

            if (element.TryGetProperty("autoStart", out var autoStartProp))
                config.AutoStart = autoStartProp.GetBoolean();

            if (element.TryGetProperty("priority", out var priorityProp))
            {
                if (Enum.TryParse<McpServerPriority>(priorityProp.GetString(), true, out var priority))
                    config.Priority = priority;
            }

            return config;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "解析 MCP 配置失败: {Name}", name);
            return null;
        }
    }

    /// <summary>
    /// 序列化单个服务器配置为 JSON 字符串
    /// </summary>
    public string SerializeServerConfig(McpServerConfig config)
    {
        var output = new Dictionary<string, object>
        {
            ["mcpServers"] = new Dictionary<string, object>
            {
                [config.Name] = SerializeServerConfigAsObject(config)
            }
        };

        return JsonSerializer.Serialize(output, _jsonOptions);
    }

    /// <summary>
    /// 序列化单个服务器配置为对象（用于嵌套序列化）
    /// </summary>
    private Dictionary<string, object> SerializeServerConfigAsObject(McpServerConfig config)
    {
        var result = new Dictionary<string, object>
        {
            ["type"] = config.TransportType switch
            {
                McpTransportType.Stdio => "stdio",
                McpTransportType.StreamableHttp => "streamableHttp",
                McpTransportType.Sse => "sse",
                _ => "stdio"
            }
        };

        // stdio 配置
        if (!string.IsNullOrEmpty(config.Command))
            result["command"] = config.Command;

        if (config.Args != null && config.Args.Count > 0)
            result["args"] = config.Args;

        if (config.Env != null && config.Env.Count > 0)
            result["env"] = config.Env;

        if (!string.IsNullOrEmpty(config.WorkingDirectory))
            result["workingDirectory"] = config.WorkingDirectory;

        // HTTP 配置
        if (config.Url != null)
            result["url"] = config.Url.ToString();

        if (config.Headers != null && config.Headers.Count > 0)
            result["headers"] = config.Headers;

        // 禁用状态
        if (config.Disabled)
            result["disabled"] = true;

        // 其他字段
        if (!string.IsNullOrEmpty(config.Description))
            result["description"] = config.Description;

        if (config.Tags != null && config.Tags.Count > 0)
            result["tags"] = config.Tags;

        // 连接配置（仅当非默认值时输出）
        if (config.ConnectionTimeoutSeconds != 30)
            result["connectionTimeout"] = config.ConnectionTimeoutSeconds;

        if (config.ShutdownTimeoutSeconds != 10)
            result["shutdownTimeout"] = config.ShutdownTimeoutSeconds;

        if (config.MaxReconnectionAttempts != 5)
            result["maxReconnectionAttempts"] = config.MaxReconnectionAttempts;

        if (config.ReconnectionIntervalMs != 1000)
            result["reconnectionInterval"] = config.ReconnectionIntervalMs;

        if (!config.AutoStart)
            result["autoStart"] = false;

        if (config.Priority != McpServerPriority.Normal)
            result["priority"] = config.Priority.ToString().ToLowerInvariant();

        return result;
    }

    /// <summary>
    /// 解析传输类型字符串
    /// </summary>
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