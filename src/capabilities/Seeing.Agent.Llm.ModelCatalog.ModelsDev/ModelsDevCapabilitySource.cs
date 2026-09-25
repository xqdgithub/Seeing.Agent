using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Llm;

namespace Seeing.Agent.Llm.ModelCatalog.ModelsDev;

/// <summary>
/// models.dev 能力源：builtin + local 常驻；remote 落盘按需读取，避免全量进内存。
/// </summary>
public sealed class ModelsDevCapabilitySource :
    IBatchEditableModelCapabilitySource,
    IRefreshableModelCapabilitySource
{
    public const string SourceId = "modelsdev";
    private const string SnapshotResourceName =
        "Seeing.Agent.Llm.ModelCatalog.ModelsDev.catalog.snapshot.json";
    private const string RemoteUrl = "https://models.dev/api.json";
    private const int RemoteTryGetCacheMax = 128;
    private const int MaxPageTake = 100;

    private readonly string _dir;
    private readonly string _remotePath;
    private readonly string _remoteBackupPath;
    private readonly string _localPath;
    private readonly string _localBackupPath;
    private readonly string _legacyCatalogPath;
    private readonly ILogger<ModelsDevCapabilitySource>? _logger;
    private readonly HttpClient _httpClient;
    private readonly object _gate = new();
    private readonly bool _allowCrossProviderFallback;
    private readonly ConcurrentDictionary<string, ModelCapabilityEntry> _remoteTryGetCache =
        new(StringComparer.OrdinalIgnoreCase);

    private ModelsDevCatalogDocument _builtin = new();
    private ModelsDevCatalogDocument _local = new();
    private List<ModelCapabilityEntry> _remoteEntries = [];
    private List<ModelCapabilityAlias> _aliases = [];
    private int _remoteEntryCount;
    private int _cachedListTotal;
    private DateTimeOffset? _lastLoadedAt;
    private string? _lastError;

    public ModelsDevCapabilitySource(
        ISeeingDirectories directories,
        ILogger<ModelsDevCapabilitySource>? logger = null,
        HttpClient? httpClient = null,
        bool allowCrossProviderFallback = true)
    {
        ArgumentNullException.ThrowIfNull(directories);
        _dir = Path.Combine(directories.UserSeeingDirectory, "model-capabilities", "modelsdev");
        Directory.CreateDirectory(_dir);
        _remotePath = Path.Combine(_dir, "remote.json");
        _remoteBackupPath = _remotePath + ".bk";
        _localPath = Path.Combine(_dir, "local.json");
        _localBackupPath = _localPath + ".bk";
        _legacyCatalogPath = Path.Combine(_dir, "catalog.json");
        _logger = logger;
        _httpClient = httpClient ?? new HttpClient();
        _allowCrossProviderFallback = allowCrossProviderFallback;
    }

    internal ModelsDevCapabilitySource(
        string storageDirectory,
        ILogger<ModelsDevCapabilitySource>? logger = null,
        HttpClient? httpClient = null,
        bool allowCrossProviderFallback = true)
    {
        _dir = storageDirectory;
        Directory.CreateDirectory(_dir);
        _remotePath = Path.Combine(_dir, "remote.json");
        _remoteBackupPath = _remotePath + ".bk";
        _localPath = Path.Combine(_dir, "local.json");
        _localBackupPath = _localPath + ".bk";
        _legacyCatalogPath = Path.Combine(_dir, "catalog.json");
        _logger = logger;
        _httpClient = httpClient ?? new HttpClient();
        _allowCrossProviderFallback = allowCrossProviderFallback;
    }

    public string Id => SourceId;
    public string DisplayName => "models.dev";

    public event EventHandler<ModelCapabilitySourceChangedEventArgs>? Changed;

    public ModelCapabilitySourceStatus GetStatus()
    {
        lock (_gate)
        {
            var curated = CuratedCountLocked();
            return new ModelCapabilitySourceStatus
            {
                LastLoadedAt = _lastLoadedAt,
                EntryCount = _cachedListTotal > 0 ? _cachedListTotal : curated + _remoteEntryCount,
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
            MigrateLegacyCatalogIfNeeded();

            // 文件 IO 全部在锁外完成，避免持有 _gate 期间做磁盘读取
            var builtin = ReadEmbeddedSnapshot();
            var local = ReadDocumentOrEmpty(_localPath);
            var remoteEntries = ModelsDevRemoteFile.EnumerateEntries(_remotePath).ToList();

            lock (_gate)
            {
                _builtin = builtin;
                _local = local;
                _remoteEntries = remoteEntries;
                _remoteEntryCount = remoteEntries.Count;
                _remoteTryGetCache.Clear();
                RebuildAliasesLocked();
                _cachedListTotal = ComputeListTotalLocked();
                _lastLoadedAt = DateTimeOffset.Now;
                _lastError = null;
            }

            RaiseChanged(ModelCapabilitySourceChangeKind.Reloaded);
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _logger?.LogWarning(ex, "加载 modelsdev catalog 失败");
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

        ModelCapabilityEntry? result;
        lock (_gate)
        {
            var resolved = ResolveAliasCore(providerId, modelId);
            var local = FindInDoc(_local, providerId, resolved);
            var builtin = FindInDoc(_builtin, providerId, resolved);
            var remote = TryGetRemoteCached(providerId, resolved);

            if (local is null && builtin is null && remote is null)
            {
                result = null;
            }
            else
            {
                var merged = ModelsDevCatalogMerge.Merge(
                    remote is null ? null : new ModelsDevCatalogDocument { Entries = [remote] },
                    builtin is null ? null : new ModelsDevCatalogDocument { Entries = [builtin] },
                    local is null ? null : new ModelsDevCatalogDocument { Entries = [local] });
                result = merged.Entries.FirstOrDefault();
            }
        }

        return ValueTask.FromResult(result);
    }

    public string ResolveAlias(string providerId, string modelId)
    {
        lock (_gate)
            return ResolveAliasCore(providerId, modelId);
    }

    public async ValueTask<IReadOnlyList<ModelCapabilityEntry>> ListEntriesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var all = new List<ModelCapabilityEntry>();
        var skip = 0;
        const int take = MaxPageTake;
        while (true)
        {
            var page = await ListEntriesPageAsync(
                new ModelCapabilityListQuery { Skip = skip, Take = take },
                cancellationToken).ConfigureAwait(false);
            all.AddRange(page.Items);
            skip += page.Items.Count;
            if (page.Items.Count < take || skip >= page.Total)
                break;
        }

        return all;
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

        List<ModelCapabilityEntry> curated;
        List<ModelCapabilityEntry> remoteEntries;
        lock (_gate)
        {
            curated = ModelsDevCatalogMerge.Merge(null, _builtin, _local).Entries?.ToList() ?? [];
            remoteEntries = _remoteEntries;
        }

        var curatedKeys = curated
            .Select(e => ModelsDevCatalogMerge.EntryKey(e.ProviderId, e.ModelId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        IEnumerable<ModelCapabilityEntry> Sequence()
        {
            foreach (var e in curated)
            {
                if (Matches(e, filter, providerFilter))
                    yield return e;
            }

            foreach (var remote in remoteEntries)
            {
                var key = ModelsDevCatalogMerge.EntryKey(remote.ProviderId, remote.ModelId);
                if (curatedKeys.Contains(key))
                    continue;

                ModelCapabilityEntry? builtin;
                ModelCapabilityEntry? local;
                lock (_gate)
                {
                    builtin = FindInDoc(_builtin, remote.ProviderId, remote.ModelId);
                    local = FindInDoc(_local, remote.ProviderId, remote.ModelId);
                }

                var merged = ModelsDevCatalogMerge.Merge(
                    new ModelsDevCatalogDocument { Entries = [remote] },
                    builtin is null ? null : new ModelsDevCatalogDocument { Entries = [builtin] },
                    local is null ? null : new ModelsDevCatalogDocument { Entries = [local] }).Entries
                    .FirstOrDefault() ?? remote;

                if (Matches(merged, filter, providerFilter))
                    yield return merged;
            }
        }

        var items = new List<ModelCapabilityEntry>(take);
        var matched = 0;
        var useCachedTotal = string.IsNullOrWhiteSpace(filter) &&
                             string.IsNullOrWhiteSpace(providerFilter);
        int cachedTotal;
        lock (_gate)
            cachedTotal = _cachedListTotal;

        foreach (var entry in Sequence())
        {
            if (matched >= skip && items.Count < take)
                items.Add(entry);
            matched++;

            // 无筛选：凑满本页即可停，Total 用缓存
            if (useCachedTotal && items.Count >= take && matched >= skip + take)
            {
                return ValueTask.FromResult(new ModelCapabilityListPage
                {
                    Items = items,
                    Total = cachedTotal,
                    Skip = skip,
                    Take = take
                });
            }
        }

        return ValueTask.FromResult(new ModelCapabilityListPage
        {
            Items = items,
            Total = useCachedTotal ? cachedTotal : matched,
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

    public ValueTask UpsertEntryAsync(
        ModelCapabilityEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();

        var snapshot = UpdateLocalLocked(() => UpsertLocalEntryLocked(entry), clearError: true);

        PersistLocal(snapshot);
        RaiseChanged(ModelCapabilitySourceChangeKind.EntriesEdited);
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteEntryAsync(
        string? providerId,
        string modelId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = UpdateLocalLocked(() =>
            _local.Entries.RemoveAll(e =>
                ProviderEquals(e.ProviderId, providerId) &&
                string.Equals(e.ModelId, modelId, StringComparison.OrdinalIgnoreCase)));

        PersistLocal(snapshot);
        RaiseChanged(ModelCapabilitySourceChangeKind.EntriesEdited);
        return ValueTask.CompletedTask;
    }

    public ValueTask UpsertAliasAsync(
        ModelCapabilityAlias alias,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alias);
        cancellationToken.ThrowIfCancellationRequested();

        var snapshot = UpdateLocalLocked(() => UpsertLocalAliasLocked(alias));

        PersistLocal(snapshot);
        RaiseChanged(ModelCapabilitySourceChangeKind.AliasesEdited);
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteAliasAsync(
        string? providerId,
        string fromModelId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = UpdateLocalLocked(() =>
            _local.Aliases.RemoveAll(a =>
                ProviderEquals(a.ProviderId, providerId) &&
                string.Equals(a.FromModelId, fromModelId, StringComparison.OrdinalIgnoreCase)));

        PersistLocal(snapshot);
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

        var snapshot = UpdateLocalLocked(() =>
            _local = new ModelsDevCatalogDocument
            {
                Entries = entries.ToList(),
                Aliases = aliases.ToList()
            });

        PersistLocal(snapshot);
        RaiseChanged(ModelCapabilitySourceChangeKind.EntriesEdited);
        return ValueTask.CompletedTask;
    }

    public async ValueTask RefreshAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var response = await _httpClient
                .GetAsync(RemoteUrl, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var json = await JsonDocument
                .ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var doc = ModelsDevApiMapper.Map(json);

            // 先落盘（锁外），再原子替换内存缓存
            WriteDocumentAtomic(_remotePath, _remoteBackupPath, doc);

            lock (_gate)
            {
                _remoteEntries = doc.Entries?.ToList() ?? [];
                _remoteEntryCount = _remoteEntries.Count;
                _remoteTryGetCache.Clear();
                _cachedListTotal = ComputeListTotalLocked();
                _lastLoadedAt = DateTimeOffset.Now;
                _lastError = null;
            }

            // 释放 Map 产生的大图
            RaiseChanged(ModelCapabilitySourceChangeKind.Reloaded);
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _logger?.LogWarning(ex, "刷新 models.dev 失败，保留本地目录");
            RaiseChanged(ModelCapabilitySourceChangeKind.RefreshFailed, ex.Message);
            throw;
        }
    }

    private ModelCapabilityEntry? TryGetRemoteCached(string? providerId, string modelId)
    {
        var cacheKey = ModelsDevCatalogMerge.EntryKey(providerId, modelId);
        if (_remoteTryGetCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var found = FindInRemoteEntries(providerId, modelId);
        if (found is null)
            return null;

        if (_remoteTryGetCache.Count >= RemoteTryGetCacheMax)
            _remoteTryGetCache.Clear();
        _remoteTryGetCache[cacheKey] = found;
        return found;
    }

    /// <summary>在已加载的 remote 条目缓存中查找（须在 <see cref="_gate"/> 内调用，无文件 IO）。</summary>
    private ModelCapabilityEntry? FindInRemoteEntries(string? providerId, string modelId)
    {
        ModelCapabilityEntry? generic = null;
        ModelCapabilityEntry? any = null;

        foreach (var entry in _remoteEntries)
        {
            if (!string.Equals(entry.ModelId, modelId, StringComparison.OrdinalIgnoreCase))
                continue;

            if (ProviderEquals(entry.ProviderId, providerId))
                return entry;

            if (string.IsNullOrWhiteSpace(entry.ProviderId))
                generic ??= entry;
            else if (_allowCrossProviderFallback)
                any ??= entry;
        }

        return generic ?? any;
    }

    private int ComputeListTotalLocked()
    {
        var curated = ModelsDevCatalogMerge.Merge(null, _builtin, _local).Entries ?? [];
        var curatedKeys = curated
            .Select(e => ModelsDevCatalogMerge.EntryKey(e.ProviderId, e.ModelId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var overlap = 0;
        foreach (var remote in _remoteEntries)
        {
            var key = ModelsDevCatalogMerge.EntryKey(remote.ProviderId, remote.ModelId);
            if (curatedKeys.Contains(key))
                overlap++;
        }

        return curated.Count + Math.Max(0, _remoteEntryCount - overlap);
    }

    private int CuratedCountLocked()
        => ModelsDevCatalogMerge.Merge(null, _builtin, _local).Entries?.Count ?? 0;

    private void RebuildAliasesLocked()
    {
        var merged = ModelsDevCatalogMerge.Merge(null, _builtin, _local);
        _aliases = merged.Aliases?.ToList() ?? [];
    }

    private void MigrateLegacyCatalogIfNeeded()
    {
        if (File.Exists(_localPath) || !File.Exists(_legacyCatalogPath))
            return;

        File.Copy(_legacyCatalogPath, _localPath, overwrite: false);
        _logger?.LogInformation(
            "已将旧 catalog.json 迁移为 local.json（覆盖层）；remote 按需读取");
    }

    private void UpsertLocalEntryLocked(ModelCapabilityEntry entry)
    {
        var idx = _local.Entries.FindIndex(e =>
            ProviderEquals(e.ProviderId, entry.ProviderId) &&
            string.Equals(e.ModelId, entry.ModelId, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
            _local.Entries[idx] = entry;
        else
            _local.Entries.Add(entry);
    }

    private void UpsertLocalAliasLocked(ModelCapabilityAlias alias)
    {
        var idx = _local.Aliases.FindIndex(a =>
            ProviderEquals(a.ProviderId, alias.ProviderId) &&
            string.Equals(a.FromModelId, alias.FromModelId, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
            _local.Aliases[idx] = alias;
        else
            _local.Aliases.Add(alias);
    }

    /// <summary>
    /// 在锁内执行本地变更、重建别名并返回待落盘快照；文件 IO 由调用方在锁外完成。
    /// </summary>
    private ModelsDevCatalogDocument UpdateLocalLocked(Action mutate, bool clearError = false)
    {
        lock (_gate)
        {
            mutate();
            RebuildAliasesLocked();
            _cachedListTotal = ComputeListTotalLocked();
            _lastLoadedAt = DateTimeOffset.Now;
            if (clearError)
                _lastError = null;
            return SnapshotLocalLocked();
        }
    }

    /// <summary>锁内快照本地文档（浅拷贝列表，避免落盘期间被并发修改）。</summary>
    private ModelsDevCatalogDocument SnapshotLocalLocked()
        => new()
        {
            Entries = _local.Entries.ToList(),
            Aliases = _local.Aliases.ToList()
        };

    /// <summary>锁外持久化本地文档。</summary>
    private void PersistLocal(ModelsDevCatalogDocument local)
        => WriteDocumentAtomic(_localPath, _localBackupPath, local);

    private static void WriteDocumentAtomic(
        string path,
        string backupPath,
        ModelsDevCatalogDocument doc)
    {
        var json = JsonSerializer.Serialize(doc, ModelsDevJson.Options);
        if (File.Exists(path))
            File.Copy(path, backupPath, overwrite: true);

        var temp = path + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, path, overwrite: true);
    }

    private static ModelsDevCatalogDocument ReadDocumentOrEmpty(string path)
    {
        if (!File.Exists(path))
            return new ModelsDevCatalogDocument();

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ModelsDevCatalogDocument>(json, ModelsDevJson.Options)
               ?? new ModelsDevCatalogDocument();
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

    private static ModelCapabilityEntry? FindInDoc(
        ModelsDevCatalogDocument doc,
        string? providerId,
        string modelId)
    {
        var exact = doc.Entries?.FirstOrDefault(e =>
            ProviderEquals(e.ProviderId, providerId) &&
            string.Equals(e.ModelId, modelId, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return exact;

        return doc.Entries?.FirstOrDefault(e =>
            string.IsNullOrWhiteSpace(e.ProviderId) &&
            string.Equals(e.ModelId, modelId, StringComparison.OrdinalIgnoreCase));
    }

    private static bool Matches(ModelCapabilityEntry e, string? filter, string? providerFilter)
    {
        if (!string.IsNullOrWhiteSpace(providerFilter) &&
            !string.Equals(e.ProviderId, providerFilter, StringComparison.OrdinalIgnoreCase))
            return false;

        if (string.IsNullOrWhiteSpace(filter))
            return true;

        return (e.ModelId?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
               || (e.ProviderId?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
               || (e.Name?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static bool ProviderEquals(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right))
            return true;
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static ModelsDevCatalogDocument ReadEmbeddedSnapshot()
    {
        var asm = typeof(ModelsDevCapabilitySource).Assembly;
        using var stream = asm.GetManifestResourceStream(SnapshotResourceName)
            ?? throw new InvalidOperationException($"缺少嵌入资源 {SnapshotResourceName}");
        return JsonSerializer.Deserialize<ModelsDevCatalogDocument>(stream, ModelsDevJson.Options)
            ?? new ModelsDevCatalogDocument();
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
