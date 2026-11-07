using Seeing.Agent.Abstractions.Llm;
using System.Text.Json.Serialization;

namespace Seeing.Provider.TypeSafeApi;

/// <summary>
/// TypeSafe（Decision）端点的连接配置。
/// <para><b>只暴露 ApiKey</b>：这是一个<b>预置的 client</b>，不是让用户从头搭的连接。
/// 端点地址、能力集、超时、代理策略、模型目录行为都是插件自身已知的事实，
/// 因此定义在代码里（见 <see cref="TypeSafeApiProvider"/>），不进配置。
/// 用户若要连自建端点，应新建一个配置型 Provider 并选 <c>typesafe</c> 协议——
/// 那条路由协议包 <c>llm.typesafe</c> 提供，与插件无关。</para>
/// <para>保留 <see cref="Models"/> 是因为它必须承载「无凭据也要有可路由模型」的种子，
/// 同时给高级用户一个在 JSON 里补充静态模型（含限额/定价）的入口；它不出现在设置页。</para>
/// <para><b>为什么不写 <c>[Required]</c></b>：本 Provider 的设计前提是
/// 「没有任何凭据也能用内置种子模型」。加必填会让设置页表单直接拦住保存，
/// 用户连「先不填 key 看看」都做不到，也无法清空已保存的 key 回到无凭据状态。</para>
/// </summary>
public sealed class TypeSafeApiOptions
{
    /// <summary>API Key（可选）。</summary>
    [JsonPropertyName("apiKey")]
    public string? ApiKey { get; set; }

    /// <summary>静态模型表（与远端目录取并集，静态优先）。</summary>
    /// <remarks>
    /// 默认给一条 <c>jev-latest</c> 种子：远端目录需要 apiKey 才能拉到，
    /// 而「装完即用」必须在**没有任何配置**时也有可路由的模型。
    /// 用户若配置了同名条目，以用户条目为准（静态表优先于远端）。
    /// </remarks>
    [JsonPropertyName("models")]
    public Dictionary<string, ModelConfig>? Models { get; set; } = CreateSeedModels();

    /// <summary>Seed decision model，保证未配置 apiKey 时目录非空。</summary>
    public const string SeedModelId = "jev-latest";

    private static Dictionary<string, ModelConfig> CreateSeedModels() => new()
    {
        [SeedModelId] = new ModelConfig
        {
            Id = SeedModelId,
            Name = "Jev Latest",
            Types = [ModelType.Decision],
        },
    };
}
