using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Mcp;
using Seeing.Agent.Abstractions.Mcp.OAuth;
using Seeing.Agent.Mcp.OAuth;
using System.Text.Json;
using System.Text.Json.Serialization;
using Seeing.Agent.Abstractions.Configuration;

namespace Seeing.Agent.Mcp.Configuration;

/// <summary>
/// MCP 配置持久化实现 - 负责配置文件的读取、写入和序列化
/// </summary>
public class McpConfigPersistence : IMcpConfigPersistence
{
    private readonly ILogger<McpConfigPersistence> _logger;
    private readonly ISeeingDirectories _directories;
    private readonly JsonSerializerOptions _jsonOptions;

    public McpConfigPersistence(
        ILogger<McpConfigPersistence> logger,
        ISeeingDirectories directories)
    {
        _logger = logger;
        _directories = directories;

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
            ConfigLevel.User => Path.Combine(_directories.UserSeeingDirectory, "mcp.json"),
            ConfigLevel.Project => Path.Combine(_directories.ProjectSeeingDirectory, "mcp.json"),
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

            // OAuth 配置（可选，缺失时行为不变）
            if (element.TryGetProperty("oauth", out var oauthProp) && oauthProp.ValueKind == JsonValueKind.Object)
                config.OAuth = ParseOAuthConfig(oauthProp);

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

        // OAuth 配置（仅当存在时输出）
        if (config.OAuth != null)
            result["oauth"] = SerializeOAuthConfigAsObject(config.OAuth);

        return result;
    }

    /// <summary>
    /// 解析 OAuth 配置（部分字段可选，缺失时保留默认值）
    /// </summary>
    private static McpOAuthConfig ParseOAuthConfig(JsonElement element)
    {
        var oauth = new McpOAuthConfig();

        if (element.TryGetProperty("authorizationEndpoint", out var authEndpointProp))
            oauth.AuthorizationEndpoint = authEndpointProp.GetString();

        if (element.TryGetProperty("tokenEndpoint", out var tokenEndpointProp))
            oauth.TokenEndpoint = tokenEndpointProp.GetString();

        if (element.TryGetProperty("clientId", out var clientIdProp))
            oauth.ClientId = clientIdProp.GetString();

        if (element.TryGetProperty("clientSecret", out var clientSecretProp))
            oauth.ClientSecret = clientSecretProp.GetString();

        if (element.TryGetProperty("scope", out var scopeProp))
            oauth.Scope = scopeProp.GetString();

        if (element.TryGetProperty("redirectUri", out var redirectUriProp))
            oauth.RedirectUri = redirectUriProp.GetString();

        if (element.TryGetProperty("disabled", out var disabledProp))
            oauth.Disabled = disabledProp.GetBoolean();

        if (element.TryGetProperty("usePkce", out var usePkceProp))
            oauth.UsePkce = usePkceProp.GetBoolean();

        if (element.TryGetProperty("autoAuthorize", out var autoAuthorizeProp))
            oauth.AutoAuthorize = autoAuthorizeProp.GetBoolean();

        return oauth;
    }

    /// <summary>
    /// 序列化 OAuth 配置为对象（仅输出非空/非默认值字段）。
    /// <para>
    /// 客户端密钥仅持久化环境变量引用（<c>env:VAR</c> / <c>${VAR}</c>）；明文密钥不会写回配置文件，
    /// 以防密钥明文落盘。请在配置中使用环境变量引用，由令牌客户端在运行时解析。
    /// </para>
    /// </summary>
    private Dictionary<string, object> SerializeOAuthConfigAsObject(McpOAuthConfig oauth)
    {
        var result = new Dictionary<string, object>();

        if (!string.IsNullOrEmpty(oauth.AuthorizationEndpoint))
            result["authorizationEndpoint"] = oauth.AuthorizationEndpoint;

        if (!string.IsNullOrEmpty(oauth.TokenEndpoint))
            result["tokenEndpoint"] = oauth.TokenEndpoint;

        if (!string.IsNullOrEmpty(oauth.ClientId))
            result["clientId"] = oauth.ClientId;

        if (!string.IsNullOrEmpty(oauth.ClientSecret))
        {
            if (McpOAuthSecretResolver.IsEnvironmentReference(oauth.ClientSecret))
            {
                result["clientSecret"] = oauth.ClientSecret;
            }
            else
            {
                _logger.LogWarning(
                    "检测到明文 OAuth ClientSecret，出于安全考虑不会写回配置文件；请改用 env:VAR 或 ${{VAR}} 引用环境变量");
            }
        }

        if (!string.IsNullOrEmpty(oauth.Scope))
            result["scope"] = oauth.Scope;

        if (!string.IsNullOrEmpty(oauth.RedirectUri))
            result["redirectUri"] = oauth.RedirectUri;

        if (oauth.Disabled)
            result["disabled"] = true;

        if (!oauth.UsePkce)
            result["usePkce"] = false;

        if (oauth.AutoAuthorize)
            result["autoAuthorize"] = true;

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