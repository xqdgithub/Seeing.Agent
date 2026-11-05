using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Llm;
using System.Collections.Immutable;

namespace Seeing.Agent.Core.Llm;

/// <summary>
/// 基于不可变快照的线程安全 Provider 注册表。
/// <para><b>来源与生命周期都是一等公民</b>：注册时随 <see cref="ProviderRegistration"/> 一起存入，
/// 使用方直接读取，不再用「owner 是否为 null」推断来源（那正是此前反复出错的根源）。</para>
/// <para><b>只释放归自己所有的实例</b>：DI 单例由容器负责释放；注册表若擅自释放它，
/// 后续任何解析都会撞 <c>ObjectDisposedException</c>。</para>
/// </summary>
public sealed class ProviderRegistry : IProviderRegistry
{
    private readonly ILogger<ProviderRegistry> _logger;
    private readonly object _writeLock = new();
    private ImmutableDictionary<string, ProviderRegistration> _registrations =
        ImmutableDictionary<string, ProviderRegistration>.Empty;

    /// <summary>注入日志构造空的 Provider 注册表。</summary>
    public ProviderRegistry(ILogger<ProviderRegistry> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public event EventHandler<ProvidersChangedEventArgs>? ProvidersChanged;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, ILlmProvider> GetProviders()
        => Snapshot().ToImmutableDictionary(pair => pair.Key, pair => pair.Value.Provider);

    /// <inheritdoc />
    public IReadOnlyDictionary<string, ProviderRegistration> GetRegistrations()
        => Snapshot();

    /// <inheritdoc />
    public ILlmProvider? GetProvider(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return Snapshot().TryGetValue(id, out var registration) ? registration.Provider : null;
    }

    /// <inheritdoc />
    public ProviderRegistration? GetRegistration(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return Snapshot().TryGetValue(id, out var registration) ? registration : null;
    }

    /// <inheritdoc />
    public ProviderOrigin? GetOrigin(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return Snapshot().TryGetValue(id, out var registration) ? registration.Origin : null;
    }

    /// <inheritdoc />
    public string? GetOwnerId(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return Snapshot().TryGetValue(id, out var registration) ? registration.OwnerId : null;
    }

    /// <inheritdoc />
    public bool IsConfigured(string id)
        => GetOrigin(id) == ProviderOrigin.Configured;

    /// <inheritdoc />
    public void Register(ProviderRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);

        var provider = registration.Provider
            ?? throw new ArgumentException("注册记录必须包含 Provider 实例。", nameof(registration));
        ArgumentException.ThrowIfNullOrWhiteSpace(provider.Id);

        ImmutableDictionary<string, ProviderRegistration> snapshot;
        ProviderRegistration? replaced;

        lock (_writeLock)
        {
            _registrations.TryGetValue(provider.Id, out replaced);
            snapshot = _registrations.SetItem(provider.Id, registration);
            Volatile.Write(ref _registrations, snapshot);
        }

        if (replaced is not null && !ReferenceEquals(replaced.Provider, provider))
        {
            _logger.LogWarning(
                "Provider {ProviderId} 已被 {PreviousOrigin} 注册，现由 {CurrentOrigin} 覆盖",
                provider.Id,
                replaced.Origin,
                registration.Origin);

            ReleaseIfOwned(replaced);
        }

        RaiseProvidersChanged(snapshot, changedProviderIds: new[] { provider.Id });
    }

    /// <inheritdoc />
    public bool Unregister(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        ProviderRegistration? removed;
        ImmutableDictionary<string, ProviderRegistration> snapshot;

        lock (_writeLock)
        {
            if (!_registrations.TryGetValue(id, out removed))
                return false;

            snapshot = _registrations.Remove(id);
            Volatile.Write(ref _registrations, snapshot);
        }

        ReleaseIfOwned(removed);
        RaiseProvidersChanged(snapshot, removedProviderIds: new[] { id });
        return true;
    }

    /// <inheritdoc />
    public int UnregisterByOwner(string ownerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);

        ProviderRegistration[] removed;
        ImmutableDictionary<string, ProviderRegistration> snapshot;
        string[] ids;

        lock (_writeLock)
        {
            ids = _registrations
                .Where(pair => string.Equals(pair.Value.OwnerId, ownerId, StringComparison.Ordinal))
                .Select(pair => pair.Key)
                .ToArray();

            if (ids.Length == 0)
                return 0;

            removed = ids.Select(id => _registrations[id]).ToArray();
            snapshot = _registrations.RemoveRange(ids);
            Volatile.Write(ref _registrations, snapshot);
        }

        foreach (var registration in removed)
            ReleaseIfOwned(registration);

        RaiseProvidersChanged(snapshot, removedProviderIds: ids);
        return removed.Length;
    }

    private ImmutableDictionary<string, ProviderRegistration> Snapshot()
        => Volatile.Read(ref _registrations);

    /// <summary>
    /// 仅当实例的生命周期归注册表时才释放。DI 容器持有的单例由容器释放。
    /// </summary>
    private void ReleaseIfOwned(ProviderRegistration registration)
    {
        if (!registration.DisposeByRegistry)
            return;

        if (registration.Provider is not IAsyncDisposable disposable)
            return;

        try
        {
            var disposal = disposable.DisposeAsync();
            if (!disposal.IsCompletedSuccessfully)
                _ = ObserveDisposalAsync(disposal, registration.Provider.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "释放 Provider {ProviderId} 时发生异常", registration.Provider.Id);
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
        IReadOnlyDictionary<string, ProviderRegistration> registrations,
        IReadOnlyList<string>? changedProviderIds = null,
        IReadOnlyList<string>? removedProviderIds = null)
        => ProvidersChanged?.Invoke(this, new ProvidersChangedEventArgs(
            registrations.ToImmutableDictionary(pair => pair.Key, pair => pair.Value.Provider),
            changedProviderIds,
            removedProviderIds));
}
