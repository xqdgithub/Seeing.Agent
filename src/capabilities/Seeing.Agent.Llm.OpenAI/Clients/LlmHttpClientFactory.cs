using Seeing.Agent.Abstractions.Llm;
using SupportHttpFactory = Seeing.Agent.Llm.Support.Http.LlmHttpClientFactory;

namespace Seeing.Agent.Llm.OpenAI.Clients;

/// <summary>
/// 为单个 Provider 创建带连接池和代理配置的 HTTP handler。
/// <para><b>实现已上提到 <see cref="SupportHttpFactory"/></b>：代理是横切基础设施，
/// Provider 插件也要用同一份语义。本类保留为协议包内的转发壳，
/// 现有调用点与测试无需改动。</para>
/// </summary>
internal static class LlmHttpClientFactory
{
    public static HttpClient Create(ProviderConfig config) => SupportHttpFactory.Create(config);

    internal static SocketsHttpHandler CreateHandler(ProviderConfig config)
        => SupportHttpFactory.CreateHandler(config);
}
