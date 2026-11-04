namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// 协议 → 缺省能力表。
/// <para>Provider 配置未写 <c>capabilities</c> 时按此表取缺省能力。
/// 刻意<b>只给 Chat</b>（typesafe 给 Decision）：若给 openai 附带 Embedding，
/// 所有 OpenAI 兼容 provider 都会自动声明嵌入能力，Provider 层校验对 embedding 恒真，
/// "缺能力时给明确异常"就退化成运行期 400（spec §7.1）。</para>
/// </summary>
public static class ProviderCapabilityDefaults
{
    /// <summary>未识别协议的兜底能力：只给 Chat。</summary>
    public const LlmCapabilityKind FallbackCapabilities = LlmCapabilityKind.Chat;

    private static readonly Dictionary<string, LlmCapabilityKind> s_byProtocol =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [ProviderTypes.OpenAi] = LlmCapabilityKind.Chat,
            [ProviderTypes.OpenAiResponses] = LlmCapabilityKind.Chat,
            [ProviderTypes.Anthropic] = LlmCapabilityKind.Chat,
            [ProviderTypes.TypeSafe] = LlmCapabilityKind.Decision,
        };

    /// <summary>协议到缺省能力的只读视图（键不区分大小写）。</summary>
    public static IReadOnlyDictionary<string, LlmCapabilityKind> ByProtocol => s_byProtocol;

    /// <summary>
    /// 取指定协议的缺省能力；空协议或未登记协议返回 <see cref="FallbackCapabilities"/>。
    /// </summary>
    public static LlmCapabilityKind Resolve(string? protocol)
    {
        if (string.IsNullOrWhiteSpace(protocol))
            return FallbackCapabilities;

        return s_byProtocol.TryGetValue(protocol.Trim(), out var capabilities)
            ? capabilities
            : FallbackCapabilities;
    }
}
