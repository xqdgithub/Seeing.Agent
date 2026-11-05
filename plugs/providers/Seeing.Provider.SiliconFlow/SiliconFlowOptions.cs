using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Seeing.Provider.SiliconFlow;

public sealed class SiliconFlowOptions
{
    [Required]
    [JsonPropertyName("apiKey")]
    public string? ApiKey { get; set; }

    /// <summary>
    /// 该端点供出的能力（配置键，小驼峰，如 <c>"chat"</c>、<c>"embedding"</c>）。
    /// <para><b>不写 = 用插件作者的默认值</b>（<see cref="SiliconFlowProvider.DefaultCapabilities"/>）。
    /// 写空数组表示「显式不供出任何能力」，用于临时停用。</para>
    /// <para>配置覆盖存在的理由：同一协议工厂可能支持多种能力，但端点未必都实现；
    /// 反过来某些私有端点也可能开放了作者默认值里没有的能力。</para>
    /// </summary>
    [JsonPropertyName("capabilities")]
    public List<string>? Capabilities { get; set; }
}
