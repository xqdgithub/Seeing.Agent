using System.Text.Json.Serialization;

namespace Seeing.Agent.Abstractions.Llm;

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

    /// <summary>
    /// 扩展元数据（插件可写入任意键值，供 UI 等消费，如免费模型标记 isFree=true）
    /// </summary>
    [JsonPropertyName("metadata")]
    public Dictionary<string, object?>? Metadata { get; set; }
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
