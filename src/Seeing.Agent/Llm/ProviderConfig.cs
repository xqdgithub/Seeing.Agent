using System.Text.Json.Serialization;

namespace Seeing.Agent.Llm;

/// <summary>
/// Provider 类型
/// </summary>
public enum ProviderType
{
    /// <summary>OpenAI</summary>
    OpenAI,

    /// <summary>Anthropic</summary>
    Anthropic
}

/// <summary>
/// Provider 配置 - 定义如何连接 LLM API
/// </summary>
public class ProviderConfig
{
    /// <summary>Provider ID（如 openai, anthropic）</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Provider 类型</summary>
    [JsonPropertyName("type")]
    public ProviderType Type { get; set; }

    /// <summary>Provider 显示名称</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>API 基础地址</summary>
    [JsonPropertyName("baseURL")]
    public string? BaseUrl { get; set; }

    /// <summary>API 密钥</summary>
    [JsonPropertyName("apiKey")]
    public string? ApiKey { get; set; }

    /// <summary>默认模型</summary>
    [JsonPropertyName("default_model")]
    public string? DefaultModel { get; set; }

    /// <summary>请求超时（毫秒）</summary>
    [JsonPropertyName("timeout")]
    public int Timeout { get; set; } = 300000; // 5 分钟

    /// <summary>最大重试次数</summary>
    [JsonPropertyName("max_retries")]
    public int MaxRetries { get; set; } = 3;

    /// <summary>自定义模型配置</summary>
    [JsonPropertyName("models")]
    public Dictionary<string, ModelConfig>? Models { get; set; }

    /// <summary>额外选项</summary>
    [JsonPropertyName("options")]
    public Dictionary<string, object>? Options { get; set; }

    /// <summary>自定义请求头</summary>
    [JsonPropertyName("headers")]
    public Dictionary<string, string>? Headers { get; set; }
}

/// <summary>
/// 预定义的 Provider 配置
/// </summary>
public static class PredefinedProviders
{
    /// <summary>OpenAI Provider</summary>
    public static ProviderConfig OpenAI(string? apiKey = null) => new()
    {
        Id = "openai",
        Type = ProviderType.OpenAI,
        Name = "OpenAI",
        BaseUrl = "https://api.openai.com/v1",
        ApiKey = apiKey,
        DefaultModel = "gpt-4o",
        Models = PredefinedModels.OpenAI
    };

    /// <summary>Anthropic Provider</summary>
    public static ProviderConfig Anthropic(string? apiKey = null) => new()
    {
        Id = "anthropic",
        Type = ProviderType.Anthropic,
        Name = "Anthropic",
        BaseUrl = "https://api.anthropic.com/v1",
        ApiKey = apiKey,
        DefaultModel = "claude-sonnet-4-20250514",
        Models = PredefinedModels.Anthropic
    };
}