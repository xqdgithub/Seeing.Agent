using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Llm;
using System.Net;

namespace Seeing.Agent.Llm.TypeSafe.Clients;

/// <summary>
/// TypeSafe 协议客户端工厂（实现框架 <see cref="ILlmClientFactory"/>，spec §7.9）。
/// <para><see cref="Create"/> 只服务 <see cref="LlmCapabilityKind.Decision"/>，
/// 其余能力返回 <c>null</c>（配置声称了别的能力时由 Router 第 5 层报错，决议 19）。</para>
/// <para>装饰器（重试/退避）的唯一应用点在工厂返回之前（spec §7.3）：
/// 原客户端自带的 429/529 退避已删除，重试经 <see cref="ILlmClientDecoratorRegistry"/> 施加。</para>
/// <para><b>调用拦截器与 OpenAI 协议工厂同权</b>：出站拦截（品牌网关塑形、动态头等）是协议无关能力，
/// 不能因为「decision 不是主流能力」就丢掉——否则 <c>llm.params</c>/<c>llm.headers</c> 这类横切机制
/// 对 decision 调用会静默失效。</para>
/// </summary>
public sealed class TypeSafeClientFactory : ILlmClientFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILlmCallInterceptorRegistry? _interceptors;
    private readonly ILlmClientDecoratorRegistry? _decorators;

    /// <summary>构造工厂。</summary>
    public TypeSafeClientFactory(
        ILoggerFactory loggerFactory,
        ILlmCallInterceptorRegistry? interceptors = null,
        ILlmClientDecoratorRegistry? decorators = null)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _interceptors = interceptors;
        _decorators = decorators;
    }

    /// <inheritdoc />
    public ILlmCapabilityClient? Create(ProviderConfig config, LlmCapabilityKind capability)
    {
        ArgumentNullException.ThrowIfNull(config);

        // 本工厂只供 Decision；协议校验在注册表侧（协议是服务键）。
        if (!capability.HasFlag(LlmCapabilityKind.Decision))
            return null;

        var client = new TypeSafeClient(
            config,
            CreateHttpClient(config),
            _loggerFactory.CreateLogger<TypeSafeClient>(),
            _interceptors);

        return _decorators is null ? client : _decorators.Apply(client, config);
    }

    /// <summary>
    /// 按 Provider 配置新建 HttpClient。
    /// <para>代理策略由 <see cref="ProviderConfig.UseProxy"/> 决定：决策服务通常在内网/直连，
    /// 插件的 Options 默认 <c>useProxy=false</c>。</para>
    /// </summary>
    private static HttpClient CreateHttpClient(ProviderConfig config)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            UseProxy = config.UseProxy
        };

        if (config.UseProxy && !string.IsNullOrWhiteSpace(config.Proxy))
            handler.Proxy = CreateProxy(config.Proxy);

        return new HttpClient(handler, disposeHandler: true)
        {
            // Timeout 必须在首次请求前设置一次（HttpClient 发出请求后不可再改）
            Timeout = TimeSpan.FromMilliseconds(
                config.Timeout > 0 ? config.Timeout : TypeSafeProtocol.DefaultTimeoutMs)
        };
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
            var username = userInfoSeparator >= 0 ? uri.UserInfo[..userInfoSeparator] : uri.UserInfo;
            var password = userInfoSeparator >= 0 ? uri.UserInfo[(userInfoSeparator + 1)..] : string.Empty;
            proxy.Credentials = new NetworkCredential(
                Uri.UnescapeDataString(username),
                Uri.UnescapeDataString(password));
        }

        return proxy;
    }
}
