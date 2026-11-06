using System.Text.Json;
using System.Text.Json.Serialization;
using Seeing.Serialization;

namespace Seeing.Gateway.Client;

internal static class GatewayJsonOptions
{
    public static JsonSerializerOptions Default { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = SeeingJson.Create(o =>
        {
            o.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            o.PropertyNameCaseInsensitive = true;
            o.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        });
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
