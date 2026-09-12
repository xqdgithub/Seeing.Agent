using System.Text.Json.Serialization;

namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// 模型 Id 别名（顶层表，唯一真相源）。
/// </summary>
public sealed class ModelCapabilityAlias
{
    [JsonPropertyName("providerId")]
    public string? ProviderId { get; init; }

    [JsonPropertyName("fromModelId")]
    public required string FromModelId { get; init; }

    [JsonPropertyName("toModelId")]
    public required string ToModelId { get; init; }
}
