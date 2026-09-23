using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.SystemOne;

namespace Seeing.Agent.SystemOne.Runtime;

/// <summary>
/// 配置驱动的 SystemOne provider：持有配置与工厂，延迟创建（且复用）底层客户端。
/// </summary>
public sealed class ConfiguredSystemOneProvider : ISystemOneProvider, IDisposable
{
    private readonly SystemOneProviderConfig _config;
    private readonly ISystemOneClientFactory _factory;
    private readonly Lazy<ISystemOneClient> _client;
    private bool _disposed;

    /// <summary>使用配置与客户端工厂构造 provider（客户端延迟创建）。</summary>
    public ConfiguredSystemOneProvider(
        SystemOneProviderConfig config,
        ISystemOneClientFactory factory,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(logger);

        _config = config;
        _factory = factory;
        _client = new Lazy<ISystemOneClient>(
            () =>
            {
                logger.LogDebug("创建 SystemOne 客户端: {ProviderId} ({Type})", _config.Id, _config.Type);
                return _factory.Create(_config);
            },
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <inheritdoc />
    public string Id => _config.Id;

    /// <inheritdoc />
    public string? Name => _config.Name;

    /// <inheritdoc />
    public string Type => _config.Type;

    /// <inheritdoc />
    public int MaxRetries => _config.MaxRetries;

    /// <inheritdoc />
    /// <exception cref="ObjectDisposedException">provider 已被释放。</exception>
    public ISystemOneClient GetClient()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ConfiguredSystemOneProvider));

        return _client.Value;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SystemOneModel>> GetModelsAsync(CancellationToken ct = default)
        => GetClient().ListModelsAsync(ct);

    /// <inheritdoc />
    public Task<bool> TestConnectionAsync(CancellationToken ct = default)
        => GetClient().TestConnectionAsync(ct);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_client.IsValueCreated && _client.Value is IDisposable disposable)
            disposable.Dispose();
    }
}
