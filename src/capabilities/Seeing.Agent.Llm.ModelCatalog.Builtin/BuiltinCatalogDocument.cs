using System.Text.Json;
using System.Text.Json.Serialization;
using Seeing.Agent.Abstractions.Llm;

namespace Seeing.Agent.Llm.ModelCatalog.Builtin;

internal sealed class BuiltinCatalogDocument
{
    [JsonPropertyName("entries")]
    public List<ModelCapabilityEntry> Entries { get; set; } = [];

    [JsonPropertyName("aliases")]
    public List<ModelCapabilityAlias> Aliases { get; set; } = [];
}

internal static class BuiltinJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };
}
