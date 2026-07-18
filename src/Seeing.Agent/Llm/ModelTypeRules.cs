namespace Seeing.Agent.Llm;

/// <summary>
/// 模型用途类型规则：缺省与按类型过滤。
/// </summary>
internal static class ModelTypeRules
{
    private static readonly ModelType[] TextOnly = [ModelType.Text];

    public static IReadOnlyList<ModelType> GetEffectiveTypes(ModelConfig config)
        => config.Types is { Count: > 0 } ? config.Types : TextOnly;

    public static bool Matches(ModelConfig config, ModelType type)
        => GetEffectiveTypes(config).Contains(type);

    public static IReadOnlyDictionary<string, ModelConfig> FilterByType(
        IReadOnlyDictionary<string, ModelConfig> models,
        ModelType type = ModelType.Text,
        string? providerId = null)
    {
        IEnumerable<KeyValuePair<string, ModelConfig>> query = models;
        if (!string.IsNullOrEmpty(providerId))
        {
            query = query.Where(kv =>
                string.Equals(kv.Value.Provider, providerId, StringComparison.OrdinalIgnoreCase)
                || kv.Key.StartsWith(providerId + "/", StringComparison.OrdinalIgnoreCase));
        }

        return query
            .Where(kv => Matches(kv.Value, type))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
    }
}
