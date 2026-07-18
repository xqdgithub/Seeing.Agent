namespace Seeing.Agent.Llm;

/// <summary>
/// 模型引用解析：仅当第一段为<strong>已知 Provider</strong>时才拆分 provider/model。
/// 避免把 HuggingFace 风格 ID（如 Qwen/Qwen3-VL-Embedding-8B）误判为 Provider。
/// </summary>
public static class ModelRef
{
    /// <summary>
    /// 组合目录键 / 完整引用：provider + "/" + modelId（modelId 可含 /）。
    /// </summary>
    public static string Format(string? providerId, string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return string.Empty;
        if (string.IsNullOrWhiteSpace(providerId))
            return modelId.Trim();
        return $"{providerId.Trim()}/{modelId.Trim()}";
    }

    /// <summary>
    /// 解析引用。仅当首段匹配 <paramref name="knownProviders"/> 时拆出 Provider。
    /// </summary>
    public static (string? ProviderId, string ModelId) Parse(
        string? reference,
        IEnumerable<string>? knownProviders)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return (null, string.Empty);

        var value = reference.Trim();
        var slash = value.IndexOf('/');
        if (slash <= 0)
            return (null, value);

        var head = value[..slash];
        var tail = value[(slash + 1)..];
        if (string.IsNullOrEmpty(tail))
            return (null, value);

        if (IsKnownProvider(head, knownProviders))
            return (head, tail);

        return (null, value);
    }

    /// <summary>首段是否为已知 Provider（忽略大小写）。</summary>
    public static bool IsKnownProvider(string? candidate, IEnumerable<string>? knownProviders)
    {
        if (string.IsNullOrWhiteSpace(candidate) || knownProviders is null)
            return false;

        foreach (var p in knownProviders)
        {
            if (string.Equals(p, candidate, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
