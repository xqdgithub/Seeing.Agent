using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Llm;

namespace Seeing.Agent.Llm.ModelCapabilities;

/// <summary>
/// 真 Manager：按 Options 门闸与 Sources Order 级联 FillEmpty。
/// </summary>
public sealed class ModelCapabilityManager : IModelCapabilityManager, IDisposable
{
    private readonly IOptionsMonitor<ModelCapabilitiesOptions> _options;
    private readonly IModelCapabilitySourceRegistry _registry;
    private readonly IReloadSignalBus _reloadBus;
    private readonly ILogger<ModelCapabilityManager>? _logger;
    private readonly object _subscriptionGate = new();
    private readonly HashSet<IModelCapabilitySource> _subscribed = new();
    private HashSet<string> _knownSourceIds;
    private bool _disposed;

    public ModelCapabilityManager(
        IOptionsMonitor<ModelCapabilitiesOptions> options,
        IModelCapabilitySourceRegistry registry,
        IReloadSignalBus reloadBus,
        ILogger<ModelCapabilityManager>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _reloadBus = reloadBus ?? throw new ArgumentNullException(nameof(reloadBus));
        _logger = logger;

        _knownSourceIds = _registry.GetSources()
            .Select(s => s.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _registry.SourcesChanged += OnSourcesChanged;
        SyncSourceSubscriptions();
        _ = _options.OnChange((_, _) =>
        {
            _ = NotifyChangedAsync(ModelCapabilitiesChangeReason.SystemConfigChanged);
        });
    }

    public event EventHandler<ModelCapabilitiesChangedEventArgs>? CapabilitiesChanged;

    public async ValueTask<ModelConfig> TryEnrichIfEnabledAsync(
        ModelConfig model,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        cancellationToken.ThrowIfCancellationRequested();

        var options = _options.CurrentValue;
        if (!options.Enabled)
            return model;

        if (!IsProviderAllowed(options, model.Provider))
            return model;

        foreach (var (source, _) in EnumerateEnabledSources(options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ModelCapabilityEntry? entry;
            try
            {
                entry = await TryGetWithFreeFallbackAsync(
                        source, model.Provider, model.Id, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "能力源 {SourceId} TryGet 失败", source.Id);
                continue;
            }

            if (entry is not null)
                ModelCapabilityFillEmpty.Apply(model, entry);
        }

        return model;
    }

    /// <summary>
    /// 先按原 Id 查找；未命中且 Id 以 <c>-free</c> 结尾时，回退到去掉后缀的 Id。
    /// </summary>
    internal static async ValueTask<ModelCapabilityEntry?> TryGetWithFreeFallbackAsync(
        IModelCapabilitySource source,
        string? providerId,
        string modelId,
        CancellationToken cancellationToken)
    {
        var entry = await source
            .TryGetAsync(providerId ?? "", modelId, cancellationToken)
            .ConfigureAwait(false);
        if (entry is not null)
            return entry;

        if (!ModelCapabilityModelIds.TryGetNonFreeFallbackId(modelId, out var baseId))
            return null;

        return await source
            .TryGetAsync(providerId ?? "", baseId, cancellationToken)
            .ConfigureAwait(false);
    }

    public IReadOnlyList<ModelCapabilitySourceRegistration> GetRegistrations()
    {
        var options = _options.CurrentValue;
        var registered = _registry.GetSources()
            .ToDictionary(s => s.Id, s => s, StringComparer.OrdinalIgnoreCase);
        var planned = BuildSourcePlan(options, registered.Keys);

        var list = new List<ModelCapabilitySourceRegistration>(planned.Count);
        foreach (var item in planned)
        {
            registered.TryGetValue(item.Id, out var source);
            list.Add(new ModelCapabilitySourceRegistration
            {
                Id = item.Id,
                DisplayName = source?.DisplayName ?? item.Id,
                Order = item.Order,
                Enabled = item.Enabled,
                Registered = source is not null,
                Status = source?.GetStatus(),
                CanList = source is IListableModelCapabilitySource,
                CanEdit = source is IEditableModelCapabilitySource,
                CanRefresh = source is IRefreshableModelCapabilitySource,
                CanBatchEdit = source is IBatchEditableModelCapabilitySource
            });
        }

        return list;
    }

    public IModelCapabilitySource? GetSource(string sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
            return null;

        return _registry.GetSources()
            .FirstOrDefault(s => string.Equals(s.Id, sourceId, StringComparison.OrdinalIgnoreCase));
    }

    public async ValueTask NotifyChangedAsync(
        ModelCapabilitiesChangeReason reason,
        IReadOnlyList<string>? affectedSourceIds = null,
        ModelCapabilitySourceChangeKind? sourceKind = null,
        bool? invalidateModelCatalog = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var ids = affectedSourceIds ?? [];
        var invalidate = invalidateModelCatalog
            ?? _options.CurrentValue.InvalidateModelCatalogOnChange;

        CapabilitiesChanged?.Invoke(this, new ModelCapabilitiesChangedEventArgs
        {
            Reason = reason,
            AffectedSourceIds = ids,
            SourceKind = sourceKind,
            Timestamp = DateTimeOffset.Now
        });

        await _reloadBus.PublishAsync(new ModelCapabilitiesChange
        {
            Reason = reason,
            AffectedSourceIds = ids,
            SourceKind = sourceKind,
            InvalidateModelCatalog = invalidate
        }, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _registry.SourcesChanged -= OnSourcesChanged;
        lock (_subscriptionGate)
        {
            foreach (var source in _subscribed)
                source.Changed -= OnSourceChanged;
            _subscribed.Clear();
        }
    }

    private void OnSourcesChanged(object? sender, EventArgs e)
    {
        var afterIds = _registry.GetSources()
            .Select(s => s.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var beforeIds = _knownSourceIds;
        var removed = beforeIds.Except(afterIds, StringComparer.OrdinalIgnoreCase).ToList();
        var added = afterIds.Except(beforeIds, StringComparer.OrdinalIgnoreCase).ToList();

        SyncSourceSubscriptions();
        _knownSourceIds = afterIds;

        if (removed.Count > 0)
        {
            _ = NotifyChangedAsync(
                ModelCapabilitiesChangeReason.SourceUnregistered,
                affectedSourceIds: removed);
        }
        else
        {
            _ = NotifyChangedAsync(
                ModelCapabilitiesChangeReason.SourceRegistered,
                affectedSourceIds: added);
        }
    }

    private void OnSourceChanged(object? sender, ModelCapabilitySourceChangedEventArgs e)
    {
        _ = NotifyChangedAsync(
            ModelCapabilitiesChangeReason.SourceDataChanged,
            affectedSourceIds: [e.SourceId],
            sourceKind: e.Kind);
    }

    private void SyncSourceSubscriptions()
    {
        var current = _registry.GetSources();
        lock (_subscriptionGate)
        {
            foreach (var source in _subscribed.ToList())
            {
                if (current.Contains(source))
                    continue;
                source.Changed -= OnSourceChanged;
                _subscribed.Remove(source);
            }

            foreach (var source in current)
            {
                if (_subscribed.Add(source))
                    source.Changed += OnSourceChanged;
            }
        }
    }

    private IEnumerable<(IModelCapabilitySource Source, int Order)> EnumerateEnabledSources(
        ModelCapabilitiesOptions options)
    {
        var registered = _registry.GetSources()
            .ToDictionary(s => s.Id, s => s, StringComparer.OrdinalIgnoreCase);
        foreach (var item in BuildSourcePlan(options, registered.Keys))
        {
            if (!item.Enabled)
                continue;
            if (!registered.TryGetValue(item.Id, out var source))
                continue;
            yield return (source, item.Order);
        }
    }

    /// <summary>
    /// 配置中的 Sources 按 Order ASC；未出现在配置中的已注册源追加为 Enabled=true、Order=max+10。
    /// </summary>
    internal static IReadOnlyList<(string Id, int Order, bool Enabled)> BuildSourcePlan(
        ModelCapabilitiesOptions options,
        IEnumerable<string> registeredIds)
    {
        var configured = options.Sources ?? [];
        var plan = new List<(string Id, int Order, bool Enabled)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in configured
                     .Where(s => !string.IsNullOrWhiteSpace(s.Id))
                     .OrderBy(s => s.Order))
        {
            if (!seen.Add(item.Id))
                continue;
            plan.Add((item.Id, item.Order, item.Enabled));
        }

        var maxOrder = plan.Count > 0 ? plan.Max(p => p.Order) : -10;
        var next = maxOrder + 10;
        foreach (var id in registeredIds)
        {
            if (!seen.Add(id))
                continue;
            plan.Add((id, next, Enabled: true));
            next += 10;
        }

        return plan.OrderBy(p => p.Order).ToList();
    }

    private static bool IsProviderAllowed(ModelCapabilitiesOptions options, string? providerId)
    {
        var providers = options.Providers;
        if (providers is null || providers.Count == 0)
            return true;

        if (providers.Any(p => p == "*"))
            return true;

        if (string.IsNullOrWhiteSpace(providerId))
            return false;

        return providers.Any(p => string.Equals(p, providerId, StringComparison.OrdinalIgnoreCase));
    }
}
