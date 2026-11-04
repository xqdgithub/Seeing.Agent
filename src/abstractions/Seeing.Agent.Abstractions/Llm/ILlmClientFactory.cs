namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// LLM 客户端工厂：按「协议 + 能力」产出客户端。
/// <para><b>装饰器（重试等）的唯一应用点在本工厂 <see cref="Create"/> 返回之前</b>（spec §7.3）。
/// 上移到 Provider 层会让直接调工厂的 plug provider 静默失去重试。</para>
/// <para>插件只依赖本契约即可提供新能力的客户端（spec §13 原则 3）。</para>
/// </summary>
public interface ILlmClientFactory
{
    /// <summary>本工厂支持的协议集合。</summary>
    IReadOnlySet<string> SupportedProtocols { get; }

    /// <summary>是否支持指定协议。</summary>
    bool SupportsProtocol(string protocol);

    /// <summary>按协议 + 能力创建一个客户端；不支持该组合返回 <c>null</c>（不抛）。</summary>
    ILlmCapabilityClient? Create(ProviderConfig config, LlmCapabilityKind capability);
}
