using System.Text.Json.Serialization;

namespace Seeing.Agent.Llm.SystemOne.Configuration;

/// <summary>
/// TypeSafe（Jev / Decision）Provider 的连接配置。
/// <para><b>为什么 SystemOne 需要自己的配置文件</b>：它是内置 Provider，装完即应可用，
/// 不应要求用户先去 <c>Providers</c> 节手写一条 <c>typesafe</c> 配置。
/// 用户若想换成自建端点，改这份文件即可；<c>Providers.typesafe</c> 存在时以它为准（见 <c>SystemOneLlmProvider</c>）。</para>
/// </summary>
public sealed class SystemOneOptions
{
    /// <summary>Provider ID（同时是模型引用前缀，如 <c>systemone/jev-latest</c>）。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = DefaultId;

    /// <summary>显示名称。</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; } = "SystemOne";

    /// <summary>基址；空时用协议缺省 <c>https://api.typesafe.ai</c>。</summary>
    [JsonPropertyName("baseURL")]
    public string? BaseUrl { get; set; }

    /// <summary>API Key。</summary>
    [JsonPropertyName("apiKey")]
    public string? ApiKey { get; set; }

    /// <summary>
    /// 该端点供出的能力。不写 = 只用 <c>decision</c>
    /// （TypeSafe 协议只实现 decision，声明其他能力会在 Router 第 5 层报错）。
    /// </summary>
    [JsonPropertyName("capabilities")]
    public List<string>? Capabilities { get; set; }

    /// <summary>是否合并远端动态模型目录（<c>GET /v1/models</c>）。默认开启。</summary>
    [JsonPropertyName("listModels")]
    public bool ListModels { get; set; } = true;

    /// <summary>静态模型表（与远端目录取并集，静态优先）。</summary>
    [JsonPropertyName("models")]
    public Dictionary<string, Abstractions.Llm.ModelConfig>? Models { get; set; }

    /// <summary>请求超时（毫秒）；非正数时用协议缺省 10s。</summary>
    [JsonPropertyName("timeout")]
    public int Timeout { get; set; }

    /// <summary>是否走系统代理。默认关闭（决策服务通常在内网/直连）。</summary>
    [JsonPropertyName("useProxy")]
    public bool UseProxy { get; set; }

    /// <summary>默认 Provider ID。</summary>
    public const string DefaultId = "systemone";
}
