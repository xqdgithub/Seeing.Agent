using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Llm;

namespace Seeing.Agent.Llm.ModelCatalog.Builtin;

/// <summary>
/// 内置精简能力目录：嵌入常见模型（含思考档位）+ local 覆盖；无 models.dev 远程。
/// </summary>
public sealed class BuiltinCapabilitySource : IBatchEditableModelCapabilitySource
{
    public const string SourceId = "builtin";
    private const string CatalogResourceName = "Seeing.Agent.Llm.ModelCatalog.Builtin.catalog.json";
    private const int MaxPageTake = 100;

    private readonly string _localPath;
    private readonly string _localBackupPath;
    private readonly ILogger<BuiltinCapabilitySource>? _logger;
    private readonly object _gate = new();

    private BuiltinCatalogDocument _embedded = new();
    private BuiltinCatalogDocument _local = new();
    private List<ModelCapabilityEntry> _entries = [];
    private List<ModelCapabilityAlias> _aliases = [];
    private DateTimeOffset? _lastLoadedAt;
    private string? _lastError;

    public BuiltinCapabilitySource(
        ISeeingDirectories directories,
        ILogger<BuiltinCapabilitySource>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(directories);
        var dir = Path.Combine(directories.UserSeeingDirectory, "model-capabilities", "builtin");
        Directory.CreateDirectory(dir);
        _localPath = Path.Combine(dir, "local.json");
        _localBackupPath = _localPath + ".bk";
        _logger = logger;
    }

    internal BuiltinCapabilitySource(string storageDirectory, ILogger<BuiltinCapabilitySource>? logger = null)
    {
        Directory.CreateDirectory(storageDirectory);
        _localPath = Path.Combine(storageDirectory, "local.json");
        _localBackupPath = _localPath + ".bk";
        _logger = logger;
    }

    public string Id => SourceId;
    public string DisplayName => "内置目录";

    public event EventHandler<ModelCapabilitySourceChangedEventArgs>? Changed;

    public ModelCapabilitySourceStatus GetStatus()
    {
        lock (_gate)
        {
            return new ModelCapabilitySourceStatus
            {
                LastLoadedAt = _lastLoadedAt,
                EntryCount = _entries.Count,
                AliasCount = _aliases.Count,
                LastError = _lastError
            };
        }
    }

    public ValueTask LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            lock (_gate)
            {
                _embedded = ReadEmbedded();
                _local = ReadLocalOrEmpty();
                RemergeLocked(clearError: true);
            }

            RaiseChanged(ModelCapabilitySourceChangeKind.Reloaded);
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _logger?.LogWarning(ex, "加载内置能力目录失败");
            RaiseChanged(ModelCapabilitySourceChangeKind.LoadFailed, ex.Message);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<ModelCapabilityEntry?> TryGetAsync(
        string providerId,
        string modelId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(modelId))
            return ValueTask.FromResult<ModelCapabilityEntry?>(null);

        lock (_gate)
        {
            var resolved = ResolveAliasCore(providerId, modelId);
            // 精确 Provider 命中 → 通用条目（空 ProviderId，跨实现匹配）
            var exact = FindEntry(providerId, resolved) ?? FindEntry(null, resolved);
            if (exact is not null)
                return ValueTask.FromResult<ModelCapabilityEntry?>(exact);

            if (ModelCapabilityModelIds.TryGetNonFreeFallbackId(resolved, out var baseId))
            {
                var fallback = FindEntry(providerId, baseId)
                               ?? FindEntry(null, baseId);
                return ValueTask.FromResult(fallback);
            }

            return ValueTask.FromResult<ModelCapabilityEntry?>(null);
        }
    }

    public string ResolveAlias(string providerId, string modelId)
    {
        lock (_gate)
            return ResolveAliasCore(providerId, modelId);
    }

    public ValueTask<IReadOnlyList<ModelCapabilityEntry>> ListEntriesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            return ValueTask.FromResult<IReadOnlyList<ModelCapabilityEntry>>(_entries.ToList());
    }

    public ValueTask<ModelCapabilityListPage> ListEntriesPageAsync(
        ModelCapabilityListQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        var skip = Math.Max(0, query.Skip);
        var take = Math.Clamp(query.Take <= 0 ? 20 : query.Take, 1, MaxPageTake);
        var filter = query.Filter?.Trim();
        var providerFilter = query.ProviderId?.Trim();

        List<ModelCapabilityEntry> all;
        lock (_gate)
            all = _entries.ToList();

        IEnumerable<ModelCapabilityEntry> q = all;
        if (!string.IsNullOrWhiteSpace(providerFilter))
            q = q.Where(e => string.Equals(e.ProviderId, providerFilter, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(filter))
        {
            q = q.Where(e =>
                (e.ModelId?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
                || (e.ProviderId?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
                || (e.Name?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        var filtered = q.ToList();
        return ValueTask.FromResult(new ModelCapabilityListPage
        {
            Items = filtered.Skip(skip).Take(take).ToList(),
            Total = filtered.Count,
            Skip = skip,
            Take = take
        });
    }

    public ValueTask<IReadOnlyList<ModelCapabilityAlias>> ListAliasesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            return ValueTask.FromResult<IReadOnlyList<ModelCapabilityAlias>>(_aliases.ToList());
    }

    public ValueTask UpsertEntryAsync(ModelCapabilityEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            UpsertLocalEntry(entry);
            PersistLocal();
            RemergeLocked(clearError: true);
        }

        RaiseChanged(ModelCapabilitySourceChangeKind.EntriesEdited);
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteEntryAsync(string? providerId, string modelId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _local.Entries.RemoveAll(e =>
                ProviderEquals(e.ProviderId, providerId) &&
                string.Equals(e.ModelId, modelId, StringComparison.OrdinalIgnoreCase));
            PersistLocal();
            RemergeLocked(clearError: true);
        }

        RaiseChanged(ModelCapabilitySourceChangeKind.EntriesEdited);
        return ValueTask.CompletedTask;
    }

    public ValueTask UpsertAliasAsync(ModelCapabilityAlias alias, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alias);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var idx = _local.Aliases.FindIndex(a =>
                ProviderEquals(a.ProviderId, alias.ProviderId) &&
                string.Equals(a.FromModelId, alias.FromModelId, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
                _local.Aliases[idx] = alias;
            else
                _local.Aliases.Add(alias);
            PersistLocal();
            RemergeLocked(clearError: true);
        }

        RaiseChanged(ModelCapabilitySourceChangeKind.AliasesEdited);
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteAliasAsync(string? providerId, string fromModelId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _local.Aliases.RemoveAll(a =>
                ProviderEquals(a.ProviderId, providerId) &&
                string.Equals(a.FromModelId, fromModelId, StringComparison.OrdinalIgnoreCase));
            PersistLocal();
            RemergeLocked(clearError: true);
        }

        RaiseChanged(ModelCapabilitySourceChangeKind.AliasesEdited);
        return ValueTask.CompletedTask;
    }

    public ValueTask ReplaceAllAsync(
        IReadOnlyList<ModelCapabilityEntry> entries,
        IReadOnlyList<ModelCapabilityAlias> aliases,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(aliases);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _local = new BuiltinCatalogDocument
            {
                Entries = entries.ToList(),
                Aliases = aliases.ToList()
            };
            PersistLocal();
            RemergeLocked(clearError: true);
        }

        RaiseChanged(ModelCapabilitySourceChangeKind.EntriesEdited);
        return ValueTask.CompletedTask;
    }

    private void RemergeLocked(bool clearError)
    {
        var merged = BuiltinCatalogMerge.Merge(null, _embedded, _local);
        _entries = merged.Entries?.ToList() ?? [];
        _aliases = merged.Aliases?.ToList() ?? [];
        _lastLoadedAt = DateTimeOffset.Now;
        if (clearError)
            _lastError = null;
    }

    private void UpsertLocalEntry(ModelCapabilityEntry entry)
    {
        var idx = _local.Entries.FindIndex(e =>
            ProviderEquals(e.ProviderId, entry.ProviderId) &&
            string.Equals(e.ModelId, entry.ModelId, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
            _local.Entries[idx] = entry;
        else
            _local.Entries.Add(entry);
    }

    private void PersistLocal()
    {
        var json = JsonSerializer.Serialize(_local, BuiltinJson.Options);
        if (File.Exists(_localPath))
            File.Copy(_localPath, _localBackupPath, overwrite: true);
        var temp = _localPath + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, _localPath, overwrite: true);
    }

    private BuiltinCatalogDocument ReadLocalOrEmpty()
    {
        if (!File.Exists(_localPath))
            return new BuiltinCatalogDocument();
        var json = File.ReadAllText(_localPath);
        return JsonSerializer.Deserialize<BuiltinCatalogDocument>(json, BuiltinJson.Options)
               ?? new BuiltinCatalogDocument();
    }

    private static BuiltinCatalogDocument ReadEmbedded()
    {
        var asm = typeof(BuiltinCapabilitySource).Assembly;
        using var stream = asm.GetManifestResourceStream(CatalogResourceName)
            ?? throw new InvalidOperationException($"缺少嵌入资源 {CatalogResourceName}");
        return JsonSerializer.Deserialize<BuiltinCatalogDocument>(stream, BuiltinJson.Options)
               ?? new BuiltinCatalogDocument();
    }

    private string ResolveAliasCore(string providerId, string modelId)
    {
        var current = modelId;
        for (var i = 0; i < 8; i++)
        {
            var alias = _aliases.FirstOrDefault(a =>
                string.Equals(a.FromModelId, current, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(a.ProviderId) ||
                 string.Equals(a.ProviderId, providerId, StringComparison.OrdinalIgnoreCase)));
            if (alias is null)
                break;
            current = alias.ToModelId;
        }

        return current;
    }

    private ModelCapabilityEntry? FindEntry(string? providerId, string modelId)
        => _entries.FirstOrDefault(e =>
            ProviderEquals(e.ProviderId, providerId) &&
            string.Equals(e.ModelId, modelId, StringComparison.OrdinalIgnoreCase));

    private static bool ProviderEquals(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right))
            return true;
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private void RaiseChanged(ModelCapabilitySourceChangeKind kind, string? error = null)
    {
        Changed?.Invoke(this, new ModelCapabilitySourceChangedEventArgs
        {
            SourceId = Id,
            Kind = kind,
            Error = error,
            Timestamp = DateTimeOffset.Now
        });
    }
}
