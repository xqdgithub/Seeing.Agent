using System.Collections.Immutable;
using Microsoft.Extensions.Logging;

namespace Seeing.Agent.Llm;

/// <summary>
/// 基于不可变快照的线程安全 Provider 注册表。
/// </summary>
public sealed class ProviderRegistry : IProviderRegistry
{
    private readonly ILogger<ProviderRegistry> _logger;
    private readonly object _writeLock = new();
    private ImmutableDictionary<string, ILlmProvider> _providers =
        ImmutableDictionary<string, ILlmProvider>.Empty;
    private ImmutableDictionary<string, string?> _owners =
        ImmutableDictionary<string, string?>.Empty;

    public ProviderRegistry(ILogger<ProviderRegistry> logger)
    {
        _logger = logger;
    }

    public event EventHandler<ProvidersChangedEventArgs>? ProvidersChanged;

    public IReadOnlyDictionary<string, ILlmProvider> GetProviders()
        => Volatile.Read(ref _providers);

    public ILlmProvider? GetProvider(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return Volatile.Read(ref _providers).TryGetValue(id, out var provider)
            ? provider
            : null;
    }

    public string? GetOwnerExtensionId(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return Volatile.Read(ref _owners).TryGetValue(id, out var ownerExtensionId)
            ? ownerExtensionId
            : null;
    }

    public void Register(ILlmProvider provider, string? ownerExtensionId = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider.Id);

        ImmutableDictionary<string, ILlmProvider> snapshot;
        ILlmProvider? replacedProvider;

        lock (_writeLock)
        {
            _providers.TryGetValue(provider.Id, out replacedProvider);
            snapshot = _providers.SetItem(provider.Id, provider);
            _owners = _owners.SetItem(provider.Id, ownerExtensionId);
            Volatile.Write(ref _providers, snapshot);
        }

        if (replacedProvider is not null)
        {
            _logger.LogWarning(
                "Provider {ProviderId} 已注册，后注册的实例将覆盖原实例",
                provider.Id);
            if (!ReferenceEquals(replacedProvider, provider))
                DisposeProvider(replacedProvider);
        }

        RaiseProvidersChanged(snapshot);
    }

    public bool Unregister(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        ILlmProvider? removed;
        ImmutableDictionary<string, ILlmProvider> snapshot;

        lock (_writeLock)
        {
            if (!_providers.TryGetValue(id, out removed))
                return false;

            snapshot = _providers.Remove(id);
            _owners = _owners.Remove(id);
            Volatile.Write(ref _providers, snapshot);
        }

        DisposeProvider(removed);
        RaiseProvidersChanged(snapshot);
        return true;
    }

    public int UnregisterByOwner(string ownerExtensionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerExtensionId);

        ILlmProvider[] removed;
        ImmutableDictionary<string, ILlmProvider> snapshot;

        lock (_writeLock)
        {
            var ids = _owners
                .Where(pair => string.Equals(
                    pair.Value,
                    ownerExtensionId,
                    StringComparison.Ordinal))
                .Select(pair => pair.Key)
                .ToArray();

            if (ids.Length == 0)
                return 0;

            removed = ids.Select(id => _providers[id]).ToArray();
            snapshot = _providers.RemoveRange(ids);
            _owners = _owners.RemoveRange(ids);
            Volatile.Write(ref _providers, snapshot);
        }

        foreach (var provider in removed)
            DisposeProvider(provider);

        RaiseProvidersChanged(snapshot);
        return removed.Length;
    }

    private void DisposeProvider(ILlmProvider provider)
    {
        if (provider is not IAsyncDisposable disposable)
            return;

        try
        {
            var disposal = disposable.DisposeAsync();
            if (!disposal.IsCompletedSuccessfully)
                _ = ObserveDisposalAsync(disposal, provider.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "释放 Provider {ProviderId} 时发生异常", provider.Id);
        }
    }

    private async Task ObserveDisposalAsync(ValueTask disposal, string providerId)
    {
        try
        {
            await disposal.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "释放 Provider {ProviderId} 时发生异常", providerId);
        }
    }

    private void RaiseProvidersChanged(
        IReadOnlyDictionary<string, ILlmProvider> providers)
        => ProvidersChanged?.Invoke(this, new ProvidersChangedEventArgs(providers));
}
