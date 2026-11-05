namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// LLM 客户端工厂：按「协议 + 能力」产出一个客户端实例。
/// <para><b>职责只有一条</b>：把 <see cref="ProviderConfig"/>（协议、端点、密钥、自定义头）翻译成能力端口实现。
/// 它<b>不声明协议</b>（协议是注册时绑定的服务键，见 <see cref="LlmClientFactoryRegistration"/>），
/// <b>也不声明能力</b>——「工厂支持某能力」不等于「端点支持该能力」
/// （同一个 <c>openai</c> 工厂既能建 embedding 客户端，也不代表 DeepSeek 的端点提供 <c>/v1/embeddings</c>）。
/// 能力由 Provider 自述，见 <see cref="ILlmProvider.Capabilities"/>。</para>
/// <para><b>装饰器（重试等）的唯一应用点在本方法返回之前</b>（spec §7.3）：
/// 上移到 Provider 层会让直接调工厂的插件 Provider 静默失去重试。</para>
/// <para><b>不是必选路径</b>：插件完全可以不实现本接口，自行构造 <see cref="ILlmCapabilityClient"/>
/// 实例并从 <see cref="ILlmProvider.GetClient"/> 返回。本接口存在的意义是让「配置驱动的 Provider」
/// （<c>Providers</c> 节里写一条 <c>{ type: "openai", baseUrl: ... }</c>）有客户端可用。</para>
/// </summary>
public interface ILlmClientFactory
{
    /// <summary>
    /// 按「协议 + 能力」创建客户端；不支持该组合返回 <c>null</c>（不抛）。
    /// <para>返回 <c>null</c> 不是错误：配置声明了某能力但该协议没实现时，
    /// 由 <c>ILlmRouter</c> 第 5 层给出可操作报错，不做静默回落。</para>
    /// </summary>
    ILlmCapabilityClient? Create(ProviderConfig config, LlmCapabilityKind capability);
}
