using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Seeing.Provider.DeepSeek;

public sealed class DeepSeekOptions
{
    [Required]
    [JsonPropertyName("apiKey")]
    public string? ApiKey { get; set; }
}
