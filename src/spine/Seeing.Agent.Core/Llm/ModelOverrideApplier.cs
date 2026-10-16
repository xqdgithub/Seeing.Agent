using Seeing.Agent.Abstractions.Llm;

namespace Seeing.Agent.Core.Llm;

/// <summary>
/// 模型元数据覆盖的纯函数应用器：把稀疏补丁合并到 <see cref="ModelConfig"/> 上，或反算差异补丁。
/// <para>不持有状态、不触达配置/IO，便于独立测试与复用。</para>
/// </summary>
internal static class ModelOverrideApplier
{
    /// <summary>
    /// 将覆盖应用到原始聚合结果，产出最终视图（含补录条目）。
    /// </summary>
    /// <param name="raw">未应用任何覆盖的 Provider 聚合结果。</param>
    /// <param name="overrides">覆盖补丁（键 = provider/modelId）。</param>
    public static Dictionary<string, ModelConfig> ApplyAll(
        IReadOnlyDictionary<string, ModelConfig> raw,
        IReadOnlyDictionary<string, ModelOverride> overrides)
    {
        var result = new Dictionary<string, ModelConfig>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, config) in raw)
        {
            result[key] = overrides.TryGetValue(key, out var patch)
                ? Apply(config, patch)
                : config;
        }

        // 补录：覆盖键在原始目录中不存在 → 以补丁构造新条目。
        foreach (var (key, patch) in overrides)
        {
            if (result.ContainsKey(key))
                continue;

            var (providerId, modelId) = SplitKey(key);
            if (string.IsNullOrEmpty(providerId) || string.IsNullOrEmpty(modelId))
                continue;

            result[key] = Apply(new ModelConfig { Id = modelId, Provider = providerId }, patch);
        }

        return result;
    }

    /// <summary>对单个配置应用稀疏补丁，返回新对象（不改动原始对象）。</summary>
    public static ModelConfig Apply(ModelConfig source, ModelOverride patch)
    {
        var result = Clone(source);

        if (patch.Types is not null)
            result.Types = new List<ModelType>(patch.Types);

        if (patch.Name is not null)
            result.Name = patch.Name;

        if (patch.ContextLimit is { } context)
        {
            result.Limit ??= new ModelLimits();
            result.Limit.Context = context;
        }

        if (patch.OutputLimit is { } output)
        {
            result.Limit ??= new ModelLimits();
            result.Limit.Output = output;
        }

        if (patch.InputModalities is not null)
        {
            result.Modalities ??= new ModelModalities();
            result.Modalities.Input = new List<string>(patch.InputModalities);
        }

        if (patch.OutputModalities is not null)
        {
            result.Modalities ??= new ModelModalities();
            result.Modalities.Output = new List<string>(patch.OutputModalities);
        }

        if (patch.IsFree is { } isFree)
        {
            result.Metadata ??= new Dictionary<string, object?>();
            result.Metadata[ModelMetadataKeys.IsFree] = isFree;
        }

        return result;
    }

    /// <summary>
    /// 计算与「无覆盖基线」的差异补丁：仅保留 <paramref name="desired"/> 与基线不同的字段。
    /// 基线为 null（补录）时保留 desired 的全部非空字段。
    /// </summary>
    public static ModelOverride Diff(ModelConfig? baseline, ModelOverride desired)
    {
        var patch = new ModelOverride();

        if (desired.Types is not null && !SequenceEqual(desired.Types, baseline?.Types))
            patch.Types = new List<ModelType>(desired.Types);

        if (desired.Name is not null && !string.Equals(desired.Name, baseline?.Name, StringComparison.Ordinal))
            patch.Name = desired.Name;

        if (desired.ContextLimit is { } context && context != (baseline?.Limit?.Context ?? 0))
            patch.ContextLimit = context;

        if (desired.OutputLimit is { } output && output != (baseline?.Limit?.Output ?? 0))
            patch.OutputLimit = output;

        if (desired.InputModalities is not null && !SequenceEqual(desired.InputModalities, baseline?.Modalities?.Input))
            patch.InputModalities = new List<string>(desired.InputModalities);

        if (desired.OutputModalities is not null && !SequenceEqual(desired.OutputModalities, baseline?.Modalities?.Output))
            patch.OutputModalities = new List<string>(desired.OutputModalities);

        if (desired.IsFree is { } isFree && isFree != ReadIsFree(baseline))
            patch.IsFree = isFree;

        return patch;
    }

    /// <summary>按 catalog key 拆分 Provider 与模型 ID（按首个 '/'）。</summary>
    public static (string? ProviderId, string ModelId) SplitKey(string key)
    {
        var slash = key.IndexOf('/');
        return slash <= 0 ? (null, key) : (key[..slash], key[(slash + 1)..]);
    }

    /// <summary>读取模型扩展元数据中的免费标记（兼容 bool / JsonElement / string）。</summary>
    public static bool ReadIsFree(ModelConfig? config)
    {
        if (config?.Metadata is null
            || !config.Metadata.TryGetValue(ModelMetadataKeys.IsFree, out var value)
            || value is null)
        {
            return false;
        }

        return value switch
        {
            bool b => b,
            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.True } => true,
            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } element
                when bool.TryParse(element.GetString(), out var parsed) => parsed,
            string s when bool.TryParse(s, out var parsed) => parsed,
            _ => false
        };
    }

    /// <summary>深拷贝模型配置（Modalities/Limit/Metadata 均独立，避免污染原始对象）。</summary>
    public static ModelConfig Clone(ModelConfig config)
        => new()
        {
            Id = config.Id,
            Name = config.Name,
            Provider = config.Provider,
            Types = config.Types is null ? new List<ModelType>() : new List<ModelType>(config.Types),
            Modalities = new ModelModalities
            {
                Input = config.Modalities?.Input is null ? new List<string>() : new List<string>(config.Modalities.Input),
                Output = config.Modalities?.Output is null ? new List<string>() : new List<string>(config.Modalities.Output)
            },
            Limit = new ModelLimits
            {
                Context = config.Limit?.Context ?? ModelLimitDefaults.Context,
                Output = config.Limit?.Output ?? ModelLimitDefaults.Output
            },
            Options = config.Options,
            Pricing = config.Pricing,
            Metadata = config.Metadata is null
                ? null
                : new Dictionary<string, object?>(config.Metadata)
        };

    private static bool SequenceEqual<T>(IReadOnlyList<T> left, IReadOnlyList<T>? right)
    {
        if (right is null)
            return left.Count == 0;

        if (left.Count != right.Count)
            return false;

        for (var i = 0; i < left.Count; i++)
        {
            if (!EqualityComparer<T>.Default.Equals(left[i], right[i]))
                return false;
        }

        return true;
    }
}
