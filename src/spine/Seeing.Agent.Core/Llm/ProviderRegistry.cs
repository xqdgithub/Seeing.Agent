using Seeing.Agent.Abstractions.Llm;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;

namespace Seeing.Agent.Core.Llm;

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

    /// <summary>
    /// 注入日志构造空的 Provider 注册表。
    /// </summary>
    public ProviderRegistry(ILogger<ProviderRegistry> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Provider 集合变更事件（注册/注销后触发，携带最新快照与变更明细）。
    /// </summary>
    public event EventHandler<ProvidersChangedEventArgs>? ProvidersChanged;

    /// <summary>
    /// 获取当前 Provider 快照（不可变字典，线程安全读取）。
    /// </summary>
    public IReadOnlyDictionary<string, ILlmProvider> GetProviders()
        => Volatile.Read(ref _providers);

    /// <summary>
    /// 按 ID 获取 Provider，未注册返回 null。
    /// </summary>
    public ILlmProvider? GetProvider(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return Volatile.Read(ref _providers).TryGetValue(id, out var provider)
            ? provider
            : null;
    }

    /// <summary>
    /// 查询 Provider 的归属扩展 ID，无归属或未注册返回 null。
    /// </summary>
    public string? GetOwnerExtensionId(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return Volatile.Read(ref _owners).TryGetValue(id, out var ownerExtensionId)
            ? ownerExtensionId
            : null;
    }

    /// <summary>
    /// 注册 Provider；同 ID 后注册者覆盖并释放被替换的实例，随后广播变更事件。
    /// </summary>
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

        RaiseProvidersChanged(snapshot, changedProviderIds: new[] { provider.Id });
    }

    /// <summary>
    /// 注销指定 ID 的 Provider 并释放实例，返回是否成功移除。
    /// </summary>
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
        RaiseProvidersChanged(snapshot, removedProviderIds: new[] { id });
        return true;
    }

    /// <summary>
    /// 注销指定扩展拥有的全部 Provider 并释放实例，返回移除数量。
    /// </summary>
    public int UnregisterByOwner(string ownerExtensionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerExtensionId);

        ILlmProvider[] removed;
        ImmutableDictionary<string, ILlmProvider> snapshot;
        string[] ids;

        lock (_writeLock)
        {
            ids = _owners
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

        RaiseProvidersChanged(snapshot, removedProviderIds: ids);
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
        IReadOnlyDictionary<string, ILlmProvider> providers,
        IReadOnlyList<string>? changedProviderIds = null,
        IReadOnlyList<string>? removedProviderIds = null)
        => ProvidersChanged?.Invoke(this, new ProvidersChangedEventArgs(
            providers,
            changedProviderIds,
            removedProviderIds));
}
