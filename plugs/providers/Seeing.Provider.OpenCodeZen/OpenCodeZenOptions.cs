using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Seeing.Provider.OpenCodeZen;

/// <summary>
/// OpenCode Zen 连接配置。
/// ApiKey 可选：免费模型无需配置，仅付费模型需要。
/// </summary>
public sealed class OpenCodeZenOptions
{
    /// <summary>
    /// OpenCode Zen API Key（可选）：可在 https://opencode.ai/auth 获取。
    /// 免费模型无需配置，仅付费模型需要。
    /// </summary>
    [JsonPropertyName("apiKey")]
    public string? ApiKey { get; set; }

    /// <summary>
    /// 用户自定义模型能力覆盖（Key=模型 ID，大小写不敏感）。
    /// 网关 /models 不返回 limit，内置预置表可能过时；模型列表调整时在此处覆盖，
    /// 无需修改代码。优先级高于内置预置表。
    /// </summary>
    [JsonPropertyName("modelCapabilities")]
    public Dictionary<string, ModelCapabilityOverride>? ModelCapabilities { get; set; }
}

/// <summary>
/// 用户自定义模型能力覆盖条目。
/// </summary>
public sealed class ModelCapabilityOverride
{
    /// <summary>上下文窗口大小</summary>
    [JsonPropertyName("context")]
    public int Context { get; set; } = OpenCodeZenModelCatalog.DefaultContext;

    /// <summary>最大输出 Token 数</summary>
    [JsonPropertyName("output")]
    public int Output { get; set; } = OpenCodeZenModelCatalog.DefaultOutput;

    /// <summary>
    /// 是否免费模型（可选）。用于手动标记未带 -free 后缀的新免费模型。
    /// 为 null 时保持模型原判定。
    /// </summary>
    [JsonPropertyName("isFree")]
    public bool? IsFree { get; set; }
}
