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
/// 思考/推理选项（与 options.thinking 对齐）
/// </summary>
public class ThinkingOptions
{
    /// <summary>是否支持思考强度（且 levels 非空时 UI/出站才启用）</summary>
    [JsonPropertyName("supported")]
    public bool Supported { get; set; }

    /// <summary>
    /// 可选默认档 key。不填表示未选档时不传思考字段（与现状一致）。
    /// </summary>
    [JsonPropertyName("default")]
    public string? Default { get; set; }

    /// <summary>模型支持的思考档位（key 原样作出站强度值）</summary>
    [JsonPropertyName("levels")]
    public List<ThinkingLevel>? Levels { get; set; }

    /// <summary>例如 enabled、disabled（legacy / Anthropic 兼容）</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "disabled";

    /// <summary>顶层预算（legacy；Anthropic budget 回落用）</summary>
    [JsonPropertyName("budgetTokens")]
    public int? BudgetTokens { get; set; }

    /// <summary>
    /// OpenAI 兼容思考回传字段名。对齐 OpenCode interleaved。
    /// 仅当值为 <c>reasoning_content</c> 时，兼容 Client 出站写出该字段；缺省不回传。
    /// Anthropic 不使用此字段（靠消息上的 ReasoningSignature 协议回传）。
    /// </summary>
    [JsonPropertyName("interleaved")]
    public string? Interleaved { get; set; }
}

/// <summary>思考强度档位</summary>
public class ThinkingLevel
{
    /// <summary>会话存储值；非关闭档时作为 reasoning_effort / effort 原样出站</summary>
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    /// <summary>UI 文案；缺省显示 key</summary>
    [JsonPropertyName("label")]
    public string? Label { get; set; }

    /// <summary>仅 Anthropic 固定预算模式使用</summary>
    [JsonPropertyName("budgetTokens")]
    public int? BudgetTokens { get; set; }
}

/// <summary>思考强度约定（关闭档等）</summary>
public static class ThinkingEffortKeys
{
    public static bool IsOff(string? key) =>
        !string.IsNullOrWhiteSpace(key) &&
        (key.Equals("disabled", StringComparison.OrdinalIgnoreCase)
         || key.Equals("off", StringComparison.OrdinalIgnoreCase)
         || key.Equals("none", StringComparison.OrdinalIgnoreCase));

    public static bool IsSupported(ThinkingOptions? thinking) =>
        thinking is { Supported: true, Levels.Count: > 0 };
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
