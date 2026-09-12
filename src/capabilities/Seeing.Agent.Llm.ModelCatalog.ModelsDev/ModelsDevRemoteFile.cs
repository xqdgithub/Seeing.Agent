using System.Text.Json;
using Seeing.Agent.Abstractions.Llm;

namespace Seeing.Agent.Llm.ModelCatalog.ModelsDev;

/// <summary>
/// 按需读取 remote.json：不长期驻留全量条目。
/// </summary>
internal static class ModelsDevRemoteFile
{
    public static int CountEntries(string path)
    {
        if (!File.Exists(path))
            return 0;

        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream);
        if (!doc.RootElement.TryGetProperty("entries", out var entries) ||
            entries.ValueKind != JsonValueKind.Array)
            return 0;

        return entries.GetArrayLength();
    }

    public static ModelCapabilityEntry? TryFind(
        string path,
        string? providerId,
        string modelId,
        bool allowCrossProviderFallback)
    {
        if (!File.Exists(path) || string.IsNullOrWhiteSpace(modelId))
            return null;

        ModelCapabilityEntry? generic = null;
        ModelCapabilityEntry? any = null;

        foreach (var entry in EnumerateEntries(path))
        {
            if (!string.Equals(entry.ModelId, modelId, StringComparison.OrdinalIgnoreCase))
                continue;

            if (ProviderEquals(entry.ProviderId, providerId))
                return entry;

            if (string.IsNullOrWhiteSpace(entry.ProviderId))
                generic ??= entry;
            else if (allowCrossProviderFallback)
                any ??= entry;
        }

        return generic ?? any;
    }

    public static IEnumerable<ModelCapabilityEntry> EnumerateEntries(string path)
    {
        if (!File.Exists(path))
            yield break;

        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream);
        if (!doc.RootElement.TryGetProperty("entries", out var entries) ||
            entries.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var el in entries.EnumerateArray())
        {
            var entry = el.Deserialize<ModelCapabilityEntry>(ModelsDevJson.Options);
            if (entry is not null && !string.IsNullOrWhiteSpace(entry.ModelId))
                yield return entry;
        }
    }

    private static bool ProviderEquals(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right))
            return true;
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }
}
