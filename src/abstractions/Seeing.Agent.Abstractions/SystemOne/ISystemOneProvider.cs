namespace Seeing.Agent.Abstractions.SystemOne;

/// <summary>配置驱动的 provider（连接实例）</summary>
public interface ISystemOneProvider
{
    string Id { get; }
    string? Name { get; }
    string Type { get; }
    int MaxRetries { get; }

    ISystemOneClient GetClient();
    Task<IReadOnlyList<SystemOneModel>> GetModelsAsync(CancellationToken cancellationToken = default);
    Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);
}

/// <summary>provider 集合管理（注册/注销/变更通知）</summary>
public interface ISystemOneProviderRegistry
{
    IReadOnlyList<ISystemOneProvider> Providers { get; }
    ISystemOneProvider? DefaultProvider { get; }

    void Register(ISystemOneProvider provider, string? ownerExtensionId = null);
    bool Unregister(string providerId);
    int UnregisterByOwner(string ownerExtensionId);

    event EventHandler? ProvidersChanged;
}
