using Seeing.Agent.Abstractions.Llm;
using System.Net;

namespace Seeing.Agent.Llm.Clients;

/// <summary>
/// 为单个 Provider 创建带连接池和代理配置的 HTTP handler。
/// </summary>
internal static class LlmHttpClientFactory
{
    public static HttpClient Create(ProviderConfig config)
        => new(CreateHandler(config), disposeHandler: true);

    internal static SocketsHttpHandler CreateHandler(ProviderConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var handler = new SocketsHttpHandler
        {
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            UseProxy = config.UseProxy
        };

        if (config.UseProxy && !string.IsNullOrWhiteSpace(config.Proxy))
            handler.Proxy = CreateProxy(config.Proxy);

        return handler;
    }

    private static IWebProxy CreateProxy(string proxyAddress)
    {
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
