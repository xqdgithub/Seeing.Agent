using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.SystemOne;

namespace Seeing.Agent.Llm.SystemOne.Clients;

/// <summary>TypeSafe provider 客户端工厂（按 type 路由，大小写不敏感）。</summary>
public sealed class TypeSafeSystemOneClientFactory : ISystemOneClientFactory
{
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>构造工厂。</summary>
    public TypeSafeSystemOneClientFactory(ILoggerFactory loggerFactory)
        => _loggerFactory = loggerFactory;

    /// <summary>支持的 provider 类型集合。</summary>
    public IReadOnlySet<string> SupportedTypes { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { SystemOneProviderTypes.TypeSafe };

    /// <summary>判断是否支持给定 provider 类型（大小写不敏感）。</summary>
    public bool SupportsType(string type) => SupportedTypes.Contains(type);

    /// <summary>创建 TypeSafe 客户端（HttpClient 由客户端持有并释放）。</summary>
    public ISystemOneClient Create(SystemOneProviderConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        // 专用 handler 工厂（原 SystemOneHttpClientFactory）已随平行栈删除：
        // 连接池与超时在此按 provider 配置就地构造，代理策略沿用框架 ProviderConfig 语义。
        var http = new HttpClient(
            new SocketsHttpHandler
            {
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                UseProxy = false
            },
            disposeHandler: true)
        {
            // Timeout 必须在首次请求前设置一次（HttpClient 发出请求后不可再改）
            Timeout = TimeSpan.FromMilliseconds(
                config.Timeout > 0 ? config.Timeout : SystemOneDefaults.DefaultTimeoutMs)
        };

        return new TypeSafeSystemOneClient(config, http, _loggerFactory.CreateLogger<TypeSafeSystemOneClient>());
    }
}
