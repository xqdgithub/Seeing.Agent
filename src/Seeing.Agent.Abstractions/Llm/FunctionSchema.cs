using System.Text.Json;
using System.Text.Json.Serialization;

namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// 函数 Schema（LLM 工具定义）
/// </summary>
public record FunctionSchema
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonIgnore]
    public string RealName { get; set; } = string.Empty;

    [JsonIgnore]
    public string ServerName { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("parameters")]
    public JsonElement? Parameters { get; set; }
}
