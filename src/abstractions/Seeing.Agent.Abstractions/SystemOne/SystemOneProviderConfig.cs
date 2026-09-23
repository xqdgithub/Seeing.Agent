using System.Text.Json.Serialization;

namespace Seeing.Agent.Abstractions.SystemOne;

/// <summary>SystemOne provider 类型标识常量（小写，用作路由键）</summary>
public static class SystemOneProviderTypes
{
    public const string TypeSafe = "typesafe";

    /// <summary>归一为规范小写；未知类型原样 Trim</summary>
    public static string Normalize(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
            return string.Empty;

        var trimmed = type.Trim();
        if (string.Equals(trimmed, TypeSafe, StringComparison.OrdinalIgnoreCase))
            return TypeSafe;
        return trimmed;
    }
}

/// <summary>SystemOne provider 连接配置（systemone.json 的值）</summary>
public class SystemOneProviderConfig
{
    [JsonPropertyName("id")]           public string Id { get; set; } = "typesafe";
    [JsonPropertyName("type")]         public string Type { get; set; } = SystemOneProviderTypes.TypeSafe;
    [JsonPropertyName("name")]         public string? Name { get; set; }
    [JsonPropertyName("baseURL")]      public string? BaseUrl { get; set; }   // 默认 https://api.typesafe.ai
    [JsonPropertyName("apiKey")]       public string? ApiKey { get; set; }    // 环境变量优先覆盖
    [JsonPropertyName("model")]        public string Model { get; set; } = "jev-latest";
    [JsonPropertyName("timeout")]      public int Timeout { get; set; } = 10000;      // 毫秒，默认 10s
    [JsonPropertyName("max_retries")]  public int MaxRetries { get; set; } = 2;
    [JsonPropertyName("retry_base_delay")] public int RetryBaseDelayMs { get; set; } = 500;
    [JsonPropertyName("retry_max_delay")]  public int RetryMaxDelayMs { get; set; } = 5000;
    [JsonPropertyName("headers")]      public Dictionary<string, string>? Headers { get; set; }
}

/// <summary>SystemOne 常量</summary>
public static class SystemOneDefaults
{
    public const string DefaultBaseUrl = "https://api.typesafe.ai";
    public const string DefaultModel = "jev-latest";
    public const string EvaluatePath = "/v1/systemone";
    public const string ModelsPath = "/v1/models";
    public const int DefaultTimeoutMs = 10000;
}
