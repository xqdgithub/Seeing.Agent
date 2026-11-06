using Seeing.Agent.Abstractions.Llm;
using SupportHttpFactory = Seeing.Agent.Llm.Support.Http.LlmHttpClientFactory;

namespace Seeing.Agent.Llm.Anthropic.Clients;

/// <summary>
/// 为单个 Provider 创建带连接池和代理配置的 HTTP handler。
/// <para><b>实现已上提到 <see cref="SupportHttpFactory"/></b>：本类此前是与 OpenAI 包
/// 逐行重复的副本，代理语义一改就得改两处。现保留为转发壳，调用点不变。</para>
/// </summary>
internal static class LlmHttpClientFactory
{
    public static HttpClient Create(ProviderConfig config) => SupportHttpFactory.Create(config);

    internal static SocketsHttpHandler CreateHandler(ProviderConfig config)
        => SupportHttpFactory.CreateHandler(config);
}
