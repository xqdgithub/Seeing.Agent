using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Seeing.Provider.SiliconFlow;

public sealed class SiliconFlowOptions
{
    [Required]
    [JsonPropertyName("apiKey")]
    public string? ApiKey { get; set; }
}
