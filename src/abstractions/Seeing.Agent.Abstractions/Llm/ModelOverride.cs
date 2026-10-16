using System.Text.Json.Serialization;

namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// 模型元数据用户覆盖节（<c>ModelOverrides</c>，用户级 <c>~/.seeing/model-overrides.json</c>）。
/// <para>节内容即字典：键 = <c>providerId/modelId</c>（与模型目录 catalog key 一致）；值 = 稀疏补丁。</para>
/// <para>仅显式设置的字段参与覆盖；未设置的字段跟随远端/默认值，远端刷新后仍可更新。</para>
/// <para>键在目录中不存在时视为补录（新增模型）。</para>
/// </summary>
/// <remarks>节类型为 <c>Dictionary&lt;string, ModelOverride&gt;</c>（文件根即字典，与 Providers 节一致）。</remarks>
public class ModelOverride
{
    /// <summary>用途类型（多标签）。</summary>
    [JsonPropertyName("types")]
    public List<ModelType>? Types { get; set; }

    /// <summary>显示名称。</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>上下文上限（token）。</summary>
    [JsonPropertyName("contextLimit")]
    public int? ContextLimit { get; set; }

    /// <summary>最大输出（token）。</summary>
    [JsonPropertyName("outputLimit")]
    public int? OutputLimit { get; set; }

    /// <summary>输入模态（如 text、image）。</summary>
    [JsonPropertyName("inputModalities")]
    public List<string>? InputModalities { get; set; }

    /// <summary>输出模态。</summary>
    [JsonPropertyName("outputModalities")]
    public List<string>? OutputModalities { get; set; }

    /// <summary>是否免费模型。</summary>
    [JsonPropertyName("isFree")]
    public bool? IsFree { get; set; }

    /// <summary>是否所有字段均为空（无可覆盖内容）。</summary>
    [JsonIgnore]
    public bool IsEmpty =>
        Types is null &&
        Name is null &&
        ContextLimit is null &&
        OutputLimit is null &&
        InputModalities is null &&
        OutputModalities is null &&
        IsFree is null;

    /// <summary>复制补丁（浅拷贝列表）。</summary>
    public ModelOverride Clone() => new()
    {
        Types = Types is null ? null : new List<ModelType>(Types),
        Name = Name,
        ContextLimit = ContextLimit,
        OutputLimit = OutputLimit,
        InputModalities = InputModalities is null ? null : new List<string>(InputModalities),
        OutputModalities = OutputModalities is null ? null : new List<string>(OutputModalities),
        IsFree = IsFree
    };
}
