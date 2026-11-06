using Seeing.Agent.Abstractions.Llm;
using Seeing.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;

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
    public static readonly JsonSerializerOptions Options = SeeingJson.Create(indented: true, o =>
    {
        o.PropertyNameCaseInsensitive = true;
        o.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    });
}
