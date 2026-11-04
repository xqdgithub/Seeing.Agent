using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Llm;
using System.Net;

namespace Seeing.Agent.Llm.SystemOne.Clients;

/// <summary>
/// TypeSafe 协议客户端工厂（实现框架 <see cref="ILlmClientFactory"/>，spec §7.9）。
/// <para><see cref="Create"/> 只服务 <see cref="LlmCapabilityKind.Decision"/>，
/// 其余能力返回 <c>null</c>（配置声称了别的能力时由 Router 第 5 层报错，决议 19）。</para>
/// <para>装饰器（重试/退避）的唯一应用点在工厂返回之前（spec §7.3）：
/// 原客户端自带的 429/529 退避已删除，重试经 <see cref="ILlmClientDecoratorRegistry"/> 施加。</para>
/// </summary>
public sealed class TypeSafeSystemOneClientFactory : ILlmClientFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILlmClientDecoratorRegistry? _decorators;

    /// <summary>构造工厂。</summary>
    public TypeSafeSystemOneClientFactory(
        ILoggerFactory loggerFactory,
        ILlmClientDecoratorRegistry? decorators = null)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _decorators = decorators;
    }

    /// <inheritdoc />
    public IReadOnlySet<string> SupportedProtocols { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ProviderTypes.TypeSafe };

    /// <inheritdoc />
    public bool SupportsProtocol(string protocol)
        => !string.IsNullOrWhiteSpace(protocol) && SupportedProtocols.Contains(protocol);

    /// <inheritdoc />
    public ILlmCapabilityClient? Create(ProviderConfig config, LlmCapabilityKind capability)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (!SupportsProtocol(config.Type))
            return null;

        if (!capability.HasFlag(LlmCapabilityKind.Decision))
            return null;

        var client = new TypeSafeSystemOneClient(
            config,
            CreateHttpClient(config),
            _loggerFactory.CreateLogger<TypeSafeSystemOneClient>());

        return _decorators is null ? client : _decorators.Apply(client, config);
    }

    /// <summary>
    /// 按 Provider 配置新建 HttpClient（原专用 <c>SystemOneHttpClientFactory</c> 删除后就地构造）。
    /// <para>代理策略不再硬编码 <c>UseProxy=false</c>：改由 <see cref="ProviderConfig.UseProxy"/> 决定，
    /// 迁移配置时必须显式写 <c>"useProxy": false</c> 才能保持原行为（spec §7.9 表）。</para>
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
