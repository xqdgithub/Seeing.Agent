using System.Text.Json.Serialization;

namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// Provider 类型标识常量（小写，用作路由键）
/// </summary>
public static class ProviderTypes
{
    /// <summary>OpenAI 兼容协议</summary>
    public const string OpenAi = "openai";

    /// <summary>Anthropic 协议</summary>
    public const string Anthropic = "anthropic";

    /// <summary>
    /// 将存量 PascalCase（如 <c>OpenAI</c>/<c>Anthropic</c>）及任意大小写变体
    /// 归一为规范小写路由键；未知类型原样保留（仅 Trim）。
    /// </summary>
    public static string Normalize(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
            return string.Empty;

        var trimmed = type.Trim();
        if (string.Equals(trimmed, OpenAi, StringComparison.OrdinalIgnoreCase))
            return OpenAi;
        if (string.Equals(trimmed, Anthropic, StringComparison.OrdinalIgnoreCase))
            return Anthropic;
        return trimmed;
    }
}

/// <summary>
/// Provider 配置 - 定义如何连接 LLM API
/// </summary>
public class ProviderConfig
{
    private string _type = string.Empty;

    /// <summary>Provider ID（如 openai, anthropic）</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Provider 类型（如 <see cref="ProviderTypes.OpenAi"/>）；反序列化时自动归一化为小写</summary>
    [JsonPropertyName("type")]
    public string Type
    {
        get => _type;
        set => _type = ProviderTypes.Normalize(value);
    }

    /// <summary>Provider 显示名称</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>API 基础地址</summary>
    [JsonPropertyName("baseURL")]
    public string? BaseUrl { get; set; }

    /// <summary>API 密钥</summary>
    [JsonPropertyName("apiKey")]
    public string? ApiKey { get; set; }

    /// <summary>
    /// 应用代理地址。为空时在 UseProxy=true 的情况下使用操作系统默认代理。
    /// 当前支持 http 和 https 代理地址，不支持 socks。
    /// </summary>
    [JsonPropertyName("proxy")]
    public string? Proxy { get; set; }

    /// <summary>是否使用代理；默认启用，Proxy 为空时使用操作系统默认代理</summary>
    [JsonPropertyName("useProxy")]
    public bool UseProxy { get; set; } = true;

    /// <summary>默认模型</summary>
    [JsonPropertyName("default_model")]
    public string? DefaultModel { get; set; }

    /// <summary>请求超时（毫秒）</summary>
    [JsonPropertyName("timeout")]
    public int Timeout { get; set; } = 300000; // 5 分钟

    /// <summary>
    /// 最大重试次数；&lt;=0 表示不限次数（仅受 <see cref="RetryTotalBudgetMs"/> 与取消约束）。
    /// </summary>
    [JsonPropertyName("max_retries")]
    public int MaxRetries { get; set; }

    /// <summary>首次重试退避（毫秒），默认 500ms</summary>
    [JsonPropertyName("retry_base_delay")]
    public int RetryBaseDelayMs { get; set; } = 500;

    /// <summary>单次重试退避上限（毫秒），默认 10000ms</summary>
    [JsonPropertyName("retry_max_delay")]
    public int RetryMaxDelayMs { get; set; } = 10000;

    /// <summary>单次 LLM 调用累计重试等待预算（毫秒），默认 120000ms（约 2 分钟）</summary>
    [JsonPropertyName("retry_total_budget")]
    public int RetryTotalBudgetMs { get; set; } = 120000;

    /// <summary>自定义模型配置（属于该 Provider 连接）</summary>
    [JsonPropertyName("models")]
    public Dictionary<string, ModelConfig>? Models { get; set; }

    /// <summary>额外选项</summary>
    [JsonPropertyName("options")]
    public Dictionary<string, object>? Options { get; set; }

    /// <summary>自定义请求头</summary>
    [JsonPropertyName("headers")]
    public Dictionary<string, string>? Headers { get; set; }
}
