using Seeing.Agent.Abstractions.Llm;
using System.Net;

namespace Seeing.Agent.Llm.Support.Http;

/// <summary>
/// LLM Provider 的 <see cref="HttpClient"/> 构造：连接池 + 代理。
/// <para><b>为什么在 Support 层</b>：代理是横切基础设施，协议包（OpenAI / Anthropic）与
/// Provider 插件（MiMo 等）都要用同一份语义。此前 OpenAI 与 Anthropic 各复制了一份，
/// 插件想支持代理只能再抄第三份——三份之间必然漂移（例如一处改了 socks 判定另一处没改）。</para>
/// <para><b>代理语义</b>：<see cref="ProviderConfig.UseProxy"/> 为 true 且
/// <see cref="ProviderConfig.Proxy"/> 为空时使用操作系统默认代理（<c>SocketsHttpHandler</c>
/// 在 <c>Proxy</c> 为 null 时自动读系统设置）；显式给了代理地址则覆盖系统设置。
/// 只支持 http/https，socks 明确拒绝而不是静默直连。</para>
/// </summary>
public static class LlmHttpClientFactory
{
    /// <summary>为单个 Provider 创建带连接池与代理配置的 <see cref="HttpClient"/>；handler 随客户端释放。</summary>
    public static HttpClient Create(ProviderConfig config)
        => new(CreateHandler(config), disposeHandler: true);

    /// <summary>创建底层 handler（测试与需要自定义 handler 包装的场景使用）。</summary>
    public static SocketsHttpHandler CreateHandler(ProviderConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var handler = new SocketsHttpHandler
        {
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            UseProxy = config.UseProxy,
        };

        // UseProxy=true 且 Proxy 为空 → Proxy 保持 null，handler 自动使用系统代理
        if (config.UseProxy && !string.IsNullOrWhiteSpace(config.Proxy))
            handler.Proxy = CreateProxy(config.Proxy);

        return handler;
    }

    /// <summary>
    /// 解析代理地址；用户名密码从 userinfo 拆出，不留在 <see cref="WebProxy.Address"/> 里
    /// （否则会被当成 URL 的一部分发给代理）。
    /// </summary>
    public static IWebProxy CreateProxy(string proxyAddress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(proxyAddress);

        if (!Uri.TryCreate(proxyAddress.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(
                "Provider proxy must be an absolute http or https URL.",
                nameof(proxyAddress));
        }

        var userInfoSeparator = uri.UserInfo.IndexOf(':');
        var proxy = new WebProxy(new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty
        }.Uri);

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var username = userInfoSeparator >= 0
                ? uri.UserInfo[..userInfoSeparator]
                : uri.UserInfo;
            var password = userInfoSeparator >= 0
                ? uri.UserInfo[(userInfoSeparator + 1)..]
                : string.Empty;
            proxy.Credentials = new NetworkCredential(
                Uri.UnescapeDataString(username),
                Uri.UnescapeDataString(password));
        }

        return proxy;
    }
}
