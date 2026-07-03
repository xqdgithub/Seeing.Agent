using System.Text.Json;
using System.Text.Json.Serialization;

namespace Seeing.Agent.Configuration;

/// <summary>
/// Gateway Client（Channel Bridge）管理配置（JSON: SeeingAgent:GatewayClients）
/// </summary>
public class GatewayClientsOptions
{
    public GatewayClientDefaults Defaults { get; set; } = new();

    public List<PluginSpec> Plugins { get; set; } = new();

    public Dictionary<string, GatewayChannelEntry> Channels { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class GatewayClientDefaults
{
    public GatewayClientConnectionOptions Gateway { get; set; } = new();

    /// <summary>所有 Channel 的默认 Agent；可被各 Channel 配置文件覆盖。</summary>
    public string? Agent { get; set; }

    /// <summary>所有 Channel 的默认 Model；可被各 Channel 配置文件覆盖。</summary>
    public string? Model { get; set; }
}

/// <summary>
/// Gateway Client 连接 Server 的配置（对应 GatewayClientOptions 子集）
/// </summary>
public class GatewayClientConnectionOptions
{
    public string BaseUrl { get; set; } = "http://127.0.0.1:8765";

    public string Transport { get; set; } = "WebSocket";

    public string WebSocketPath { get; set; } = "/api/gateway/ws";

    public string? ApiKey { get; set; }

    public string? Timeout { get; set; }
}

/// <summary>
/// 单个 Channel 在 seeing.json 中的注册条目（仅元数据）。
/// Channel 参数与 Gateway 连接配置见 <c>.seeing/gateway-clients/{channelId}.json</c>。
/// </summary>
public class GatewayChannelEntry
{
    public bool Enabled { get; set; }

    public string? PluginSpec { get; set; }

    /// <summary>已弃用：仅用于从旧版 seeing.json 迁移，新保存不再写入。</summary>
    public GatewayClientConnectionOptions? Gateway { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>已弃用：仅用于从旧版 seeing.json 迁移，新保存不再写入。</summary>
    public Dictionary<string, JsonElement>? Options { get; set; }
}

/// <summary>
/// Channel Bridge 进程运行时状态（.seeing/gateway-clients/{id}.state.json）
/// </summary>
public class GatewayClientRuntimeState
{
    public int? ProcessId { get; set; }

    public string Status { get; set; } = GatewayClientStatuses.Stopped;

    public DateTimeOffset? StartedAt { get; set; }

    public string? LastError { get; set; }

    public int? HealthPort { get; set; }
}

public static class GatewayClientStatuses
{
    public const string Stopped = "Stopped";
    public const string Starting = "Starting";
    public const string Running = "Running";
    public const string Error = "Error";
    public const string Disabled = "Disabled";
}
