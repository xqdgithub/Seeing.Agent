using System.Text.Json;
using System.Text.Json.Serialization;
using Seeing.Agent.Abstractions.Llm;

namespace Seeing.Agent.Llm.ModelCatalog.ModelsDev;

internal sealed class ModelsDevCatalogDocument
{
    [JsonPropertyName("entries")]
    public List<ModelCapabilityEntry> Entries { get; set; } = [];

    [JsonPropertyName("aliases")]
    public List<ModelCapabilityAlias> Aliases { get; set; } = [];
}

internal static class ModelsDevJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };
}
