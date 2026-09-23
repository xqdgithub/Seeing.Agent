using Seeing.Agent.Abstractions.SystemOne;
using Microsoft.Extensions.Logging;

namespace Seeing.Agent.SystemOne.Clients;

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

        var http = SystemOneHttpClientFactory.Create(config);
        return new TypeSafeSystemOneClient(config, http, _loggerFactory.CreateLogger<TypeSafeSystemOneClient>());
    }
}
