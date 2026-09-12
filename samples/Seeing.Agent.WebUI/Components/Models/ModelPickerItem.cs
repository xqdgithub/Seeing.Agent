using System.Text.Json;
using Seeing.Agent.Abstractions.Llm;

namespace Seeing.Agent.WebUI.Components.Models;

/// <summary>
/// 模型选择器行数据：从 <see cref="ModelConfig"/> 映射，供 Dropdown / Modal / Badge 共用。
/// </summary>
public sealed class ModelPickerItem
{
    public required string Key { get; init; }
    public required string Provider { get; init; }
    public required string ModelId { get; init; }
    public required string DisplayName { get; init; }
    public IReadOnlyList<ModelType> Types { get; init; } = [];
    public int Context { get; init; }
    public int Output { get; init; }
    public bool IsFree { get; init; }
    public bool SupportsThinking { get; init; }

    /// <summary>Badge 主文案：模型显示名</summary>
    public string BadgeLabel => DisplayName;

    /// <summary>Badge 次要文案：Provider（小字灰度）</summary>
    public string? BadgeProvider =>
        string.IsNullOrEmpty(Provider) ? null : Provider;

    /// <summary>
    /// 从目录构建选项。
    /// <paramref name="filterType"/> 为 null 时不过滤类型；否则按有效类型命中（Types 空视为 Text）。
    /// </summary>
    public static IReadOnlyList<ModelPickerItem> Build(
        IReadOnlyDictionary<string, ModelConfig> models,
        ModelType? filterType = ModelType.Text,
        string? providerId = null)
    {
        if (models.Count == 0)
            return [];

        var list = new List<ModelPickerItem>(models.Count);
        foreach (var (key, config) in models)
        {
            if (!string.IsNullOrEmpty(providerId)
                && !MatchesProvider(key, config, providerId))
            {
                continue;
            }

            var types = GetEffectiveTypes(config);
            if (filterType is { } ft && !types.Contains(ft))
                continue;

            list.Add(From(key, config, types));
        }

        return list
            .OrderBy(i => i.Provider, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static ModelPickerItem From(string key, ModelConfig config)
        => From(key, config, GetEffectiveTypes(config));

    public static bool IsFreeModel(ModelConfig config)
    {
        if (config.Metadata is null
            || !config.Metadata.TryGetValue(ModelMetadataKeys.IsFree, out var value)
            || value is null)
        {
            return false;
        }

        return value switch
        {
            bool b => b,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.String } element
                when bool.TryParse(element.GetString(), out var parsed) => parsed,
            string s when bool.TryParse(s, out var parsed) => parsed,
            _ => false
        };
    }

    private static ModelPickerItem From(string key, ModelConfig config, IReadOnlyList<ModelType> types)
    {
        var provider = config.Provider;
        var modelId = config.Id;
        if (string.IsNullOrEmpty(provider) || string.IsNullOrEmpty(modelId))
        {
            var slash = key.IndexOf('/');
            if (slash > 0)
            {
                if (string.IsNullOrEmpty(provider))
                    provider = key[..slash];
                if (string.IsNullOrEmpty(modelId))
                    modelId = key[(slash + 1)..];
            }
        }

        if (string.IsNullOrEmpty(modelId))
            modelId = key;
        if (string.IsNullOrEmpty(provider))
            provider = "其他";

        var display = !string.IsNullOrWhiteSpace(config.Name) ? config.Name! : modelId;

        return new ModelPickerItem
        {
            Key = key,
            Provider = provider,
            ModelId = modelId,
            DisplayName = display,
            Types = types,
            Context = config.Limit?.Context ?? 0,
            Output = config.Limit?.Output ?? 0,
            IsFree = IsFreeModel(config),
            SupportsThinking = ThinkingEffortKeys.IsSupported(config.Options?.Thinking)
        };
    }

    private static IReadOnlyList<ModelType> GetEffectiveTypes(ModelConfig config)
        => config.Types is { Count: > 0 } ? config.Types : [ModelType.Text];

    private static bool MatchesProvider(string key, ModelConfig config, string providerId)
        => string.Equals(config.Provider, providerId, StringComparison.OrdinalIgnoreCase)
           || key.StartsWith(providerId + "/", StringComparison.OrdinalIgnoreCase);
}
