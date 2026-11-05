using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Seeing.Provider.MiMo;

public sealed class MiMoOptions
{
    [Required]
    [JsonPropertyName("apiKey")]
    public string? ApiKey { get; set; }

    /// <summary>
    /// 该端点供出的能力（配置键，小驼峰）。不写 = 用作者默认值
    /// （<see cref="MiMoProvider.DefaultCapabilities"/>，当前只有 chat）。
    /// </summary>
    [JsonPropertyName("capabilities")]
    public List<string>? Capabilities { get; set; }
}
