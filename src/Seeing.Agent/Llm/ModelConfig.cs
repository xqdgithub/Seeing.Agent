using System.Text.Json.Serialization;

namespace Seeing.Agent.Llm;

/// <summary>
/// 模型配置（与 ModelScope / Provider.models 条目结构对齐）
/// </summary>
public class ModelConfig
{
    /// <summary>模型 ID（API 调用使用的标识，如 gpt-4o、qwen3-coder-next）</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>显示名称</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>所属 Provider ID</summary>
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    /// <summary>用途类型（多标签）。空/缺失时有效类型为 [Text]。</summary>
    [JsonPropertyName("types")]
    public List<ModelType> Types { get; set; } = new();

    /// <summary>输入/输出模态（字符串列表，如 text、image）</summary>
    [JsonPropertyName("modalities")]
    public ModelModalities Modalities { get; set; } = new();

    /// <summary>上下文与输出上限</summary>
    [JsonPropertyName("limit")]
    public ModelLimits Limit { get; set; } = new();

    /// <summary>扩展选项（如思考链）</summary>
    [JsonPropertyName("options")]
    public ModelOptions? Options { get; set; }

    /// <summary>定价信息（可选）</summary>
    [JsonPropertyName("pricing")]
    public ModelPricing? Pricing { get; set; }
}

/// <summary>
/// 模态列表（与 JSON modalities.input / modalities.output 对齐）
/// </summary>
public class ModelModalities
{
    [JsonPropertyName("input")]
    public List<string> Input { get; set; } = new();

    [JsonPropertyName("output")]
    public List<string> Output { get; set; } = new();
}

/// <summary>
/// 模型扩展选项
/// </summary>
public class ModelOptions
{
    [JsonPropertyName("thinking")]
    public ThinkingOptions? Thinking { get; set; }
}

/// <summary>
/// 思考/推理预算（与 options.thinking 对齐）
/// </summary>
public class ThinkingOptions
{
    /// <summary>例如 enabled、disabled</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "disabled";

    [JsonPropertyName("budgetTokens")]
    public int? BudgetTokens { get; set; }
}

/// <summary>
/// 模型限制（与 limit.context / limit.output 对齐）
/// </summary>
public class ModelLimits
{
    [JsonPropertyName("context")]
    public int Context { get; set; } = 4096;

    [JsonPropertyName("output")]
    public int Output { get; set; } = 4096;
}

/// <summary>
/// 模型定价
/// </summary>
public class ModelPricing
{
    [JsonPropertyName("input")]
    public double Input { get; set; }

    [JsonPropertyName("output")]
    public double Output { get; set; }

    [JsonPropertyName("cache_read")]
    public double? CacheRead { get; set; }

    [JsonPropertyName("cache_write")]
    public double? CacheWrite { get; set; }
}

/// <summary>
/// 预定义的模型配置
/// </summary>
public static class PredefinedModels
{
    /// <summary>OpenAI 模型</summary>
    public static readonly Dictionary<string, ModelConfig> OpenAI = new()
    {
        ["gpt-4o"] = new()
        {
            Id = "gpt-4o",
            Name = "GPT-4o",
            Provider = "openai",
            Modalities = new ModelModalities
            {
                Input = ["text", "image", "audio"],
                Output = ["text", "audio"]
            },
            Limit = new ModelLimits { Context = 128000, Output = 16384 },
            Pricing = new ModelPricing { Input = 2.5, Output = 10 }
        },
        ["gpt-4o-mini"] = new()
        {
            Id = "gpt-4o-mini",
            Name = "GPT-4o Mini",
            Provider = "openai",
            Modalities = new ModelModalities
            {
                Input = ["text", "image", "audio"],
                Output = ["text"]
            },
            Limit = new ModelLimits { Context = 128000, Output = 16384 },
            Pricing = new ModelPricing { Input = 0.15, Output = 0.6 }
        },
        ["gpt-4-turbo"] = new()
        {
            Id = "gpt-4-turbo",
            Name = "GPT-4 Turbo",
            Provider = "openai",
            Modalities = new ModelModalities
            {
                Input = ["text", "image"],
                Output = ["text"]
            },
            Limit = new ModelLimits { Context = 128000, Output = 4096 },
            Pricing = new ModelPricing { Input = 10, Output = 30 }
        },
        ["o1"] = new()
        {
            Id = "o1",
            Name = "o1",
            Provider = "openai",
            Modalities = new ModelModalities
            {
                Input = ["text"],
                Output = ["text"]
            },
            Options = new ModelOptions
            {
                Thinking = new ThinkingOptions { Type = "enabled", BudgetTokens = 8192 }
            },
            Limit = new ModelLimits { Context = 200000, Output = 100000 },
            Pricing = new ModelPricing { Input = 15, Output = 60 }
        }
    };

    /// <summary>Anthropic 模型</summary>
    public static readonly Dictionary<string, ModelConfig> Anthropic = new()
    {
        ["claude-sonnet-4-20250514"] = new()
        {
            Id = "claude-sonnet-4-20250514",
            Name = "Claude Sonnet 4",
            Provider = "anthropic",
            Modalities = new ModelModalities
            {
                Input = ["text", "image"],
                Output = ["text"]
            },
            Options = new ModelOptions
            {
                Thinking = new ThinkingOptions { Type = "enabled", BudgetTokens = 8192 }
            },
            Limit = new ModelLimits { Context = 200000, Output = 16000 },
            Pricing = new ModelPricing { Input = 3, Output = 15 }
        },
        ["claude-3-5-sonnet-20241022"] = new()
        {
            Id = "claude-3-5-sonnet-20241022",
            Name = "Claude 3.5 Sonnet",
            Provider = "anthropic",
            Modalities = new ModelModalities
            {
                Input = ["text", "image"],
                Output = ["text"]
            },
            Limit = new ModelLimits { Context = 200000, Output = 8192 },
            Pricing = new ModelPricing { Input = 3, Output = 15 }
        },
        ["claude-3-5-haiku-20241022"] = new()
        {
            Id = "claude-3-5-haiku-20241022",
            Name = "Claude 3.5 Haiku",
            Provider = "anthropic",
            Modalities = new ModelModalities
            {
                Input = ["text"],
                Output = ["text"]
            },
            Limit = new ModelLimits { Context = 200000, Output = 8192 },
            Pricing = new ModelPricing { Input = 0.8, Output = 4 }
        },
        ["claude-3-opus-20240229"] = new()
        {
            Id = "claude-3-opus-20240229",
            Name = "Claude 3 Opus",
            Provider = "anthropic",
            Modalities = new ModelModalities
            {
                Input = ["text", "image"],
                Output = ["text"]
            },
            Limit = new ModelLimits { Context = 200000, Output = 4096 },
            Pricing = new ModelPricing { Input = 15, Output = 75 }
        }
    };

    /// <summary>获取所有预定义模型（键为 provider/modelId）</summary>
    public static Dictionary<string, ModelConfig> GetAll()
    {
        var result = new Dictionary<string, ModelConfig>();
        foreach (var (key, value) in OpenAI)
            result[$"openai/{key}"] = value;
        foreach (var (key, value) in Anthropic)
            result[$"anthropic/{key}"] = value;
        return result;
    }
}
