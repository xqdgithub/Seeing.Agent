using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Seeing.Provider.MiMo;

public sealed class MiMoOptions
{
    [Required]
    [JsonPropertyName("apiKey")]
    public string? ApiKey { get; set; }
}
