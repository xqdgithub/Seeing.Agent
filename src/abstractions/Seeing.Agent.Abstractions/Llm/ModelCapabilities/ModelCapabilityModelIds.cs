namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// 模型 Id 约定：OpenCode Zen 等免费线以 <c>-free</c> 后缀标识，能力可回退到无后缀条目。
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
}
