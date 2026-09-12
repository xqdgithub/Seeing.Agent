using System.Text.Json;
using Seeing.Agent.Abstractions.Llm;

namespace Seeing.Agent.Llm.ModelCatalog.ModelsDev;

/// <summary>
/// 将 models.dev <c>api.json</c> 映射为本地 <see cref="ModelsDevCatalogDocument"/>。
/// </summary>
internal static class ModelsDevApiMapper
{
    public static ModelsDevCatalogDocument Map(JsonDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var entries = new List<ModelCapabilityEntry>();

        foreach (var providerProp in document.RootElement.EnumerateObject())
        {
            if (providerProp.Value.ValueKind != JsonValueKind.Object)
                continue;
            if (!providerProp.Value.TryGetProperty("models", out var models) ||
                models.ValueKind != JsonValueKind.Object)
                continue;

            var providerId = MapProviderId(providerProp.Name);
            foreach (var modelProp in models.EnumerateObject())
            {
                var entry = MapModel(providerId, modelProp.Name, modelProp.Value);
                if (entry is not null)
                    entries.Add(entry);
            }
        }

        return new ModelsDevCatalogDocument { Entries = entries };
    }

    public static ModelsDevCatalogDocument Map(string json)
    {
        using var document = JsonDocument.Parse(json);
        return Map(document);
    }

    internal static string MapProviderId(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
            return providerId;

        if (string.Equals(providerId, "opencode", StringComparison.OrdinalIgnoreCase))
            return "opencode-zen";

        return providerId.ToLowerInvariant();
    }

    private static ModelCapabilityEntry? MapModel(
        string providerId,
        string modelKey,
        JsonElement model)
    {
        if (model.ValueKind != JsonValueKind.Object)
            return null;

        var modelId = model.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
            ? idEl.GetString()
            : modelKey;
        if (string.IsNullOrWhiteSpace(modelId))
            return null;

        string? name = null;
        if (model.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
            name = nameEl.GetString();

        return new ModelCapabilityEntry
        {
            ProviderId = providerId,
            ModelId = modelId,
            Name = name,
            Limit = MapLimit(model),
            Modalities = MapModalities(model),
            Options = MapOptions(model),
            Pricing = MapPricing(model)
        };
    }

    private static ModelCapabilityLimits? MapLimit(JsonElement model)
    {
        if (!model.TryGetProperty("limit", out var limit) || limit.ValueKind != JsonValueKind.Object)
            return null;

        int? context = null;
        int? output = null;
        if (limit.TryGetProperty("context", out var ctx) && ctx.TryGetInt32(out var c))
            context = c;
        if (limit.TryGetProperty("output", out var outEl) && outEl.TryGetInt32(out var o))
            output = o;

        if (context is null && output is null)
            return null;

        return new ModelCapabilityLimits { Context = context, Output = output };
    }

    private static ModelModalities? MapModalities(JsonElement model)
    {
        if (model.TryGetProperty("modalities", out var modalities) &&
            modalities.ValueKind == JsonValueKind.Object)
        {
            return new ModelModalities
            {
                Input = ReadStringArray(modalities, "input"),
                Output = ReadStringArray(modalities, "output")
            };
        }

        if (model.TryGetProperty("attachment", out var attachment) &&
            attachment.ValueKind is JsonValueKind.True)
        {
            return new ModelModalities
            {
                Input = ["image"],
                Output = []
            };
        }

        return null;
    }

    private static ModelOptions? MapOptions(JsonElement model)
    {
        var reasoning = model.TryGetProperty("reasoning", out var reasoningEl) &&
                        reasoningEl.ValueKind == JsonValueKind.True;
        if (!reasoning)
            return null;

        string? interleaved = null;
        if (model.TryGetProperty("interleaved", out var interleavedEl))
        {
            if (interleavedEl.ValueKind == JsonValueKind.Object &&
                interleavedEl.TryGetProperty("field", out var fieldEl) &&
                fieldEl.ValueKind == JsonValueKind.String &&
                string.Equals(fieldEl.GetString(), "reasoning_content", StringComparison.Ordinal))
            {
                interleaved = "reasoning_content";
            }
        }

        return new ModelOptions
        {
            Thinking = new ThinkingOptions
            {
                Supported = true,
                // 不发明 Levels：档位由 builtin / local 提供
                Interleaved = interleaved
            }
        };
    }

    private static ModelPricing? MapPricing(JsonElement model)
    {
        if (!model.TryGetProperty("cost", out var cost) || cost.ValueKind != JsonValueKind.Object)
            return null;

        double input = 0;
        double output = 0;
        double? cacheRead = null;
        double? cacheWrite = null;
        var hasAny = false;

        if (cost.TryGetProperty("input", out var inEl) && inEl.TryGetDouble(out var i))
        {
            input = i;
            hasAny = true;
        }

        if (cost.TryGetProperty("output", out var outEl) && outEl.TryGetDouble(out var o))
        {
            output = o;
            hasAny = true;
        }

        if (cost.TryGetProperty("cache_read", out var crEl) && crEl.TryGetDouble(out var cr))
        {
            cacheRead = cr;
            hasAny = true;
        }

        if (cost.TryGetProperty("cache_write", out var cwEl) && cwEl.TryGetDouble(out var cw))
        {
            cacheWrite = cw;
            hasAny = true;
        }

        if (!hasAny)
            return null;

        return new ModelPricing
        {
            Input = input,
            Output = output,
            CacheRead = cacheRead,
            CacheWrite = cacheWrite
        };
    }

    private static List<string> ReadStringArray(JsonElement parent, string name)
    {
        var list = new List<string>();
        if (!parent.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var s = item.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                    list.Add(s);
            }
        }

        return list;
    }
}
