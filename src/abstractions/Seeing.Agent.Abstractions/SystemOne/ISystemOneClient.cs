namespace Seeing.Agent.Abstractions.SystemOne;

/// <summary>SystemOne 协议客户端</summary>
public interface ISystemOneClient
{
    string ProviderId { get; }
    string ProviderType { get; }

    Task<SystemOneResponse> EvaluateAsync(SystemOneRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SystemOneModel>> ListModelsAsync(CancellationToken cancellationToken = default);
    Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);
}

/// <summary>协议工厂：按 provider 类型创建客户端</summary>
public interface ISystemOneClientFactory
{
    IReadOnlySet<string> SupportedTypes { get; }
    bool SupportsType(string type);
    ISystemOneClient Create(SystemOneProviderConfig config);
}
