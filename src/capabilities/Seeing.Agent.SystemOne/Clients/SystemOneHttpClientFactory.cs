using Seeing.Agent.Abstractions.SystemOne;

namespace Seeing.Agent.SystemOne.Clients;

/// <summary>
/// 为单个 SystemOne provider 创建带连接池配置的 HttpClient。
/// </summary>
public static class SystemOneHttpClientFactory
{
    /// <summary>创建 HttpClient；Timeout 取 config.Timeout，非正数时回落到 DefaultTimeoutMs。</summary>
    public static HttpClient Create(SystemOneProviderConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return new HttpClient(CreateHandler(config), disposeHandler: true)
        {
            // Timeout 必须在首次请求前设置一次（HttpClient 发出请求后不可再改）
            Timeout = TimeSpan.FromMilliseconds(
                config.Timeout > 0 ? config.Timeout : SystemOneDefaults.DefaultTimeoutMs)
        };
    }

    /// <summary>创建 SocketsHttpHandler（连接池空闲/生命周期上限；本期不使用代理）。</summary>
    internal static SocketsHttpHandler CreateHandler(SystemOneProviderConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return new SocketsHttpHandler
        {
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            UseProxy = false
        };
    }
}
