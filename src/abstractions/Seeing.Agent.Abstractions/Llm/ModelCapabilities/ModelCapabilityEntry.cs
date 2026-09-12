using System.Text.Json.Serialization;

namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// 模型能力条目（与 <see cref="ModelConfig"/> 可对齐的补全集）。
/// </summary>
public sealed class ModelCapabilityEntry
{
    /// <summary>可空；空表示跨 Provider 通用条目。</summary>
    [JsonPropertyName("providerId")]
    public string? ProviderId { get; init; }

    [JsonPropertyName("modelId")]
    public required string ModelId { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("limit")]
    public ModelCapabilityLimits? Limit { get; init; }

    [JsonPropertyName("modalities")]
    public ModelModalities? Modalities { get; init; }

    /// <summary>与 <see cref="ModelConfig.Options"/> 对齐；Thinking 仅放于此。</summary>
    [JsonPropertyName("options")]
    public ModelOptions? Options { get; init; }

    [JsonPropertyName("pricing")]
    public ModelPricing? Pricing { get; init; }
}

/// <summary>
/// 能力源侧 Limit（字段可空，仅填有值的一侧）。
/// </summary>
public sealed class ModelCapabilityLimits
{
    [JsonPropertyName("context")]
    public int? Context { get; init; }

    [JsonPropertyName("output")]
    public int? Output { get; init; }
}
