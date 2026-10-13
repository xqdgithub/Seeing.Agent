using System.Text.Json.Serialization;

namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// 模型启用清单（allowlist）配置：按 Provider 分组列出用户希望使用的模型 ID。
/// <para>Provider 仍返回全部可用模型；本清单决定哪些模型进入用户可见/可选的目录子集。</para>
/// <para>Provider 键缺失或列表为空表示该 Provider 全部隐藏。</para>
/// </summary>
public class ModelSelectionConfig
{
    /// <summary>Provider ID → 启用的模型 ID 列表（匹配 <see cref="ModelConfig.Id"/>）。</summary>
    [JsonPropertyName("providers")]
    public Dictionary<string, List<string>> Providers { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}
