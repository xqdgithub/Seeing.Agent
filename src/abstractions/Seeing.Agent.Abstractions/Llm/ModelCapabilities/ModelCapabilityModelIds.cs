namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// 模型 Id 约定：OpenCode Zen 等免费线以 <c>-free</c> 后缀标识，能力可回退到无后缀条目；
/// SiliconFlow 等网关使用 <c>org/model</c> 形式时，可回退到最后一段 leaf id。
/// </summary>
public static class ModelCapabilityModelIds
{
    public const string FreeSuffix = "-free";

    /// <summary>
    /// 若 <paramref name="modelId"/> 以 <c>-free</c> 结尾，返回去掉后缀的 Id；否则返回 false。
    /// </summary>
    public static bool TryGetNonFreeFallbackId(string? modelId, out string baseModelId)
    {
        baseModelId = "";
        if (string.IsNullOrWhiteSpace(modelId))
            return false;

        var id = modelId.Trim();
        if (id.Length <= FreeSuffix.Length)
            return false;

        if (!id.EndsWith(FreeSuffix, StringComparison.OrdinalIgnoreCase))
            return false;

        baseModelId = id[..^FreeSuffix.Length];
        return !string.IsNullOrWhiteSpace(baseModelId);
    }

    /// <summary>
    /// 若 <paramref name="modelId"/> 含 <c>/</c>，返回最后一段（如 <c>deepseek-ai/DeepSeek-V4-Flash</c> → <c>DeepSeek-V4-Flash</c>）；否则返回 false。
    /// </summary>
    public static bool TryGetLeafModelId(string? modelId, out string leafModelId)
    {
        leafModelId = "";
        if (string.IsNullOrWhiteSpace(modelId))
            return false;

        var id = modelId.Trim();
        var slash = id.LastIndexOf('/');
        if (slash < 0 || slash >= id.Length - 1)
            return false;

        leafModelId = id[(slash + 1)..].Trim();
        return leafModelId.Length > 0
               && !string.Equals(leafModelId, id, StringComparison.Ordinal);
    }
}
