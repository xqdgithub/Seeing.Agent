using Seeing.Agent.Abstractions.Llm;

namespace Seeing.Agent.Llm.ModelCatalog.ModelsDev;

/// <summary>
/// 三层目录合并：remote → builtin → local（后者非空字段覆盖前者；Levels 整表替换）。
/// </summary>
internal static class ModelsDevCatalogMerge
{
    public static ModelsDevCatalogDocument Merge(
        ModelsDevCatalogDocument? remote,
        ModelsDevCatalogDocument? builtin,
        ModelsDevCatalogDocument? local)
    {
        var entries = new Dictionary<string, ModelCapabilityEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var layer in new[] { remote, builtin, local })
        {
            if (layer?.Entries is null)
                continue;
            foreach (var entry in layer.Entries)
            {
                if (string.IsNullOrWhiteSpace(entry.ModelId))
                    continue;
                var key = EntryKey(entry.ProviderId, entry.ModelId);
                entries[key] = entries.TryGetValue(key, out var existing)
                    ? OverlayEntry(existing, entry)
                    : CloneEntry(entry);
            }
        }

        var aliases = new Dictionary<string, ModelCapabilityAlias>(StringComparer.OrdinalIgnoreCase);
        foreach (var layer in new[] { remote, builtin, local })
        {
            if (layer?.Aliases is null)
                continue;
            foreach (var alias in layer.Aliases)
            {
                if (string.IsNullOrWhiteSpace(alias.FromModelId) ||
                    string.IsNullOrWhiteSpace(alias.ToModelId))
                    continue;
                aliases[AliasKey(alias.ProviderId, alias.FromModelId)] = alias;
            }
        }

        return new ModelsDevCatalogDocument
        {
            Entries = entries.Values.OrderBy(e => e.ProviderId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.ModelId, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Aliases = aliases.Values.OrderBy(a => a.FromModelId, StringComparer.OrdinalIgnoreCase).ToList()
        };
    }

    internal static string EntryKey(string? providerId, string modelId)
        => $"{providerId?.Trim() ?? ""}|{modelId.Trim()}";

    internal static string AliasKey(string? providerId, string fromModelId)
        => $"{providerId?.Trim() ?? ""}|{fromModelId.Trim()}";

    private static ModelCapabilityEntry OverlayEntry(ModelCapabilityEntry lower, ModelCapabilityEntry higher)
        => new()
        {
            ProviderId = higher.ProviderId ?? lower.ProviderId,
            ModelId = higher.ModelId,
            Name = FirstNonEmpty(higher.Name, lower.Name),
            Limit = OverlayLimit(lower.Limit, higher.Limit),
            Modalities = higher.Modalities ?? CloneModalities(lower.Modalities),
            Options = OverlayOptions(lower.Options, higher.Options),
            Pricing = higher.Pricing ?? ClonePricing(lower.Pricing)
        };

    private static ModelCapabilityLimits? OverlayLimit(
        ModelCapabilityLimits? lower,
        ModelCapabilityLimits? higher)
    {
        if (higher is null)
            return CloneLimit(lower);
        if (lower is null)
            return CloneLimit(higher);

        return new ModelCapabilityLimits
        {
            Context = higher.Context ?? lower.Context,
            Output = higher.Output ?? lower.Output
        };
    }

    private static ModelOptions? OverlayOptions(ModelOptions? lower, ModelOptions? higher)
    {
        if (higher is null)
            return CloneOptions(lower);
        if (lower is null)
            return CloneOptions(higher);

        return new ModelOptions
        {
            Thinking = OverlayThinking(lower.Thinking, higher.Thinking)
        };
    }

    private static ThinkingOptions? OverlayThinking(ThinkingOptions? lower, ThinkingOptions? higher)
    {
        if (higher is null)
            return CloneThinking(lower);
        if (lower is null)
            return CloneThinking(higher);

        return new ThinkingOptions
        {
            Supported = higher.Supported,
            Default = FirstNonEmpty(higher.Default, lower.Default),
            Levels = higher.Levels is { Count: > 0 }
                ? CloneLevels(higher.Levels)
                : CloneLevels(lower.Levels),
            Type = higher.Type,
            BudgetTokens = higher.BudgetTokens ?? lower.BudgetTokens,
            Interleaved = FirstNonEmpty(higher.Interleaved, lower.Interleaved)
        };
    }

    private static ModelCapabilityEntry CloneEntry(ModelCapabilityEntry source)
        => new()
        {
            ProviderId = source.ProviderId,
            ModelId = source.ModelId,
            Name = source.Name,
            Limit = CloneLimit(source.Limit),
            Modalities = CloneModalities(source.Modalities),
            Options = CloneOptions(source.Options),
            Pricing = ClonePricing(source.Pricing)
        };

    private static ModelCapabilityLimits? CloneLimit(ModelCapabilityLimits? source)
        => source is null
            ? null
            : new ModelCapabilityLimits { Context = source.Context, Output = source.Output };

    private static ModelModalities? CloneModalities(ModelModalities? source)
        => source is null
            ? null
            : new ModelModalities
            {
                Input = source.Input?.ToList() ?? [],
                Output = source.Output?.ToList() ?? []
            };

    private static ModelOptions? CloneOptions(ModelOptions? source)
        => source is null
            ? null
            : new ModelOptions { Thinking = CloneThinking(source.Thinking) };

    private static ThinkingOptions? CloneThinking(ThinkingOptions? source)
    {
        if (source is null)
            return null;
        return new ThinkingOptions
        {
            Supported = source.Supported,
            Default = source.Default,
            Levels = CloneLevels(source.Levels),
            Type = source.Type,
            BudgetTokens = source.BudgetTokens,
            Interleaved = source.Interleaved
        };
    }

    private static List<ThinkingLevel>? CloneLevels(IReadOnlyList<ThinkingLevel>? levels)
        => levels is null
            ? null
            : levels.Select(l => new ThinkingLevel
            {
                Key = l.Key,
                Label = l.Label,
                BudgetTokens = l.BudgetTokens
            }).ToList();

    private static ModelPricing? ClonePricing(ModelPricing? source)
        => source is null
            ? null
            : new ModelPricing
            {
                Input = source.Input,
                Output = source.Output,
                CacheRead = source.CacheRead,
                CacheWrite = source.CacheWrite
            };

    private static string? FirstNonEmpty(string? higher, string? lower)
        => !string.IsNullOrWhiteSpace(higher) ? higher : lower;
}
