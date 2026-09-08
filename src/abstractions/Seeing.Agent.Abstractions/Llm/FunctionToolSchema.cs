using System.Text.Json.Serialization;

namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// 函数工具 Schema（LLM tools 数组元素）
/// </summary>
public record FunctionToolSchema
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public FunctionSchema Function { get; set; } = new();
}
