using Seeing.Agent.Abstractions.SystemOne;

namespace Seeing.Agent.SystemOne.Tests;

internal sealed class StubSystemOneClient : ISystemOneClient, IDisposable
{
    public string ProviderId { get; set; } = "stub";
    public string ProviderType { get; set; } = SystemOneProviderTypes.TypeSafe;
    public int DisposeCount { get; private set; }
    public Func<SystemOneRequest, CancellationToken, Task<SystemOneResponse>>? OnEvaluate { get; set; }
    public Func<CancellationToken, Task<IReadOnlyList<SystemOneModel>>>? OnListModels { get; set; }

    public Task<SystemOneResponse> EvaluateAsync(
        SystemOneRequest request, CancellationToken cancellationToken = default)
        => OnEvaluate?.Invoke(request, cancellationToken) ?? Task.FromResult(new SystemOneResponse());

    public Task<IReadOnlyList<SystemOneModel>> ListModelsAsync(CancellationToken cancellationToken = default)
        => OnListModels?.Invoke(cancellationToken)
           ?? Task.FromResult<IReadOnlyList<SystemOneModel>>(Array.Empty<SystemOneModel>());

    public Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    public void Dispose() => DisposeCount++;
}

internal sealed class StubSystemOneClientFactory : ISystemOneClientFactory
{
    public IReadOnlySet<string> SupportedTypes { get; set; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { SystemOneProviderTypes.TypeSafe };

    public List<SystemOneProviderConfig> CreatedConfigs { get; } = new();
    public Func<SystemOneProviderConfig, ISystemOneClient>? ClientFactory { get; set; }
    public int CreateCount { get; private set; }

    public bool SupportsType(string type) => SupportedTypes.Contains(type);

    public ISystemOneClient Create(SystemOneProviderConfig config)
    {
        CreateCount++;
        CreatedConfigs.Add(config);
        return ClientFactory?.Invoke(config)
            ?? new StubSystemOneClient { ProviderId = config.Id, ProviderType = config.Type };
    }
}

internal sealed class StubSystemOneProvider : ISystemOneProvider, IDisposable
{
    public string Id { get; set; } = "stub";
    public string? Name { get; set; }
    public string Type { get; set; } = SystemOneProviderTypes.TypeSafe;
    public int MaxRetries { get; set; } = 2;
    public StubSystemOneClient Client { get; } = new();
    public bool Disposed { get; private set; }

    public ISystemOneClient GetClient() => Client;

    public Task<IReadOnlyList<SystemOneModel>> GetModelsAsync(CancellationToken cancellationToken = default)
        => Client.ListModelsAsync(cancellationToken);

    public Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
        => Client.TestConnectionAsync(cancellationToken);

    public void Dispose() => Disposed = true;
}

internal sealed class FakeSystemOneProviderRegistry : ISystemOneProviderRegistry
{
    public List<ISystemOneProvider> Items { get; } = new();

    public ISystemOneProvider? DefaultProvider { get; set; }

    public IReadOnlyList<ISystemOneProvider> Providers => Items;

    public void Register(ISystemOneProvider provider, string? ownerExtensionId = null)
        => Items.Add(provider);

    public bool Unregister(string providerId)
        => Items.RemoveAll(p => p.Id == providerId) > 0;

    public int UnregisterByOwner(string ownerExtensionId) => 0;

    public event EventHandler? ProvidersChanged
    {
        add { }
        remove { }
    }
}
