using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.SystemOne;
using Seeing.Agent.SystemOne.Runtime;

namespace Seeing.Agent.SystemOne.Configuration;

/// <summary>
/// SystemOne provider 管理器：从配置 + 环境变量重建本模块自有的 provider 集合，
/// 同时保留外部 <see cref="Register"/> 进来的 provider；线程安全，实现 <see cref="IDisposable"/>。
/// </summary>
public sealed class SystemOneProviderManager : ISystemOneProviderRegistry, IDisposable
{
    /// <summary>本模块自身 owner 标识（Deactivate 按此注销）。</summary>
    private const string OwnerId = "systemone";

    private readonly SystemOneConfigStore _configStore;
    private readonly IReadOnlyList<ISystemOneClientFactory> _factories;
    private readonly ILogger<SystemOneProviderManager> _logger;
    private readonly List<(ISystemOneProvider Provider, string Owner)> _entries = new();
    private readonly object _gate = new();

    private ISystemOneProvider? _defaultProvider;
    private bool _disposed;

    /// <summary>注入配置存取器、客户端工厂集合与日志。</summary>
    public SystemOneProviderManager(
        SystemOneConfigStore configStore,
        IEnumerable<ISystemOneClientFactory> factories,
        ILogger<SystemOneProviderManager> logger)
    {
        _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
        _factories = (factories ?? throw new ArgumentNullException(nameof(factories))).ToArray();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public IReadOnlyList<ISystemOneProvider> Providers
    {
        get
        {
            lock (_gate)
                return _entries.Select(e => e.Provider).ToArray();
        }
    }

    /// <inheritdoc />
    public ISystemOneProvider? DefaultProvider
    {
        get
        {
            lock (_gate)
                return _defaultProvider;
        }
    }

    /// <inheritdoc />
    public event EventHandler? ProvidersChanged;

    /// <summary>
    /// 从配置 + 环境变量重建“本模块自有的” provider 集合（幂等）。
    /// <para>只替换 owner == <c>systemone</c> 的条目；外部 Register 的 provider 原样保留。</para>
    /// <para>
    /// 已知边界：本方法在重建后会<b>同步释放</b>被替换的旧 provider（见方法末尾），因此配置热重载
    /// 瞬间若仍有在途请求持有旧 provider 的客户端，可能被中止（<see cref="ObjectDisposedException"/>
    /// 或取消）；当前接受该边界，不做延迟回收。
    /// </para>
    /// </summary>
    /// <exception cref="ObjectDisposedException">管理器已被释放。</exception>
    public void Reload()
    {
        ThrowIfDisposed();

        Dictionary<string, SystemOneProviderConfig> fileEntries;
        try
        {
            fileEntries = _configStore.Read();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取 SystemOne 配置失败，仅使用内置默认 provider");
            fileEntries = new Dictionary<string, SystemOneProviderConfig>(StringComparer.OrdinalIgnoreCase);
        }

        // 以 config.Id 为权威 id（仅当其为空时回退字典键），统一归一化 type；
        // 同 id 后写覆盖先写（字典大小写不敏感），确保最终 work 内不存在重复 Id。
        var work = new Dictionary<string, SystemOneProviderConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, config) in fileEntries)
        {
            if (config is null)
                continue;

            var id = string.IsNullOrWhiteSpace(config.Id) ? key : config.Id;
            if (string.IsNullOrWhiteSpace(id))
                continue;

            config.Id = id;
            config.Type = SystemOneProviderTypes.Normalize(config.Type);
            work[id] = config;
        }

        // 内置 typesafe 兜底，随后统一套用环境变量覆盖
        if (!work.ContainsKey("typesafe"))
            work["typesafe"] = PredefinedSystemOneProviders.CreateDefaultTypeSafe();

        var typeSafe = work["typesafe"];
        typeSafe.Type = SystemOneProviderTypes.Normalize(
            string.IsNullOrWhiteSpace(typeSafe.Type) ? SystemOneProviderTypes.TypeSafe : typeSafe.Type);

        var envBaseUrl = SystemOneEnvironmentConfig.ResolveBaseUrl();
        if (!string.IsNullOrWhiteSpace(envBaseUrl))
            typeSafe.BaseUrl = envBaseUrl;

        var envApiKey = SystemOneEnvironmentConfig.ResolveApiKey();
        if (!string.IsNullOrWhiteSpace(envApiKey))
            typeSafe.ApiKey = envApiKey;

        var envModel = SystemOneEnvironmentConfig.ResolveModel();
        if (!string.IsNullOrWhiteSpace(envModel))
            typeSafe.Model = envModel;

        var rebuilt = new List<ISystemOneProvider>();
        foreach (var (_, config) in work)
        {
            var factory = SystemOneClientFactoryResolver.Find(_factories, config.Type);
            if (factory is null)
            {
                _logger.LogWarning(
                    "跳过未知 SystemOne provider 类型: {ProviderId} ({Type})", config.Id, config.Type);
                continue;
            }

            if (string.IsNullOrWhiteSpace(config.ApiKey))
            {
                _logger.LogWarning("SystemOne provider {ProviderId} 缺少 ApiKey，已跳过注册", config.Id);
                continue;
            }

            rebuilt.Add(new ConfiguredSystemOneProvider(config, factory, _logger));
            _logger.LogDebug("已加载 SystemOne provider: {ProviderId} ({Type})", config.Id, config.Type);
        }

        List<ISystemOneProvider> replaced;
        var collided = new List<ISystemOneProvider>();
        lock (_gate)
        {
            ThrowIfDisposed();

            replaced = _entries.Where(e => e.Owner == OwnerId).Select(e => e.Provider).ToList();
            _entries.RemoveAll(e => e.Owner == OwnerId);

            foreach (var provider in rebuilt)
            {
                var collision = _entries.Any(e =>
                    string.Equals(e.Provider.Id, provider.Id, StringComparison.OrdinalIgnoreCase));
                if (collision)
                {
                    _logger.LogInformation(
                        "SystemOne provider {ProviderId} 已由外部注册，跳过配置驱动加载", provider.Id);
                    collided.Add(provider);
                    continue;
                }

                _entries.Add((provider, OwnerId));
            }

            _defaultProvider = ResolveDefaultProviderLocked();
        }

        foreach (var provider in replaced)
            DisposeProviderSafe(provider);
        foreach (var provider in collided)
            DisposeProviderSafe(provider);

        RaiseProvidersChanged();
    }

    /// <inheritdoc />
    /// <exception cref="ObjectDisposedException">管理器已被释放。</exception>
    public void Register(ISystemOneProvider provider, string? ownerExtensionId = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(provider);

        var owner = ownerExtensionId ?? string.Empty;
        ISystemOneProvider? replaced = null;

        lock (_gate)
        {
            ThrowIfDisposed();

            var index = _entries.FindIndex(e =>
                string.Equals(e.Provider.Id, provider.Id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                replaced = _entries[index].Provider;
                _entries[index] = (provider, owner);
            }
            else
            {
                _entries.Add((provider, owner));
            }

            _defaultProvider = ResolveDefaultProviderLocked();
        }

        DisposeProviderSafe(replaced);
        RaiseProvidersChanged();
    }

    /// <inheritdoc />
    /// <exception cref="ObjectDisposedException">管理器已被释放。</exception>
    public bool Unregister(string providerId)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(providerId))
            return false;

        ISystemOneProvider? removed;
        lock (_gate)
        {
            ThrowIfDisposed();

            var index = _entries.FindIndex(e =>
                string.Equals(e.Provider.Id, providerId, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                return false;

            removed = _entries[index].Provider;
            _entries.RemoveAt(index);
            _defaultProvider = ResolveDefaultProviderLocked();
        }

        DisposeProviderSafe(removed);
        RaiseProvidersChanged();
        return true;
    }

    /// <inheritdoc />
    /// <exception cref="ObjectDisposedException">管理器已被释放。</exception>
    public int UnregisterByOwner(string ownerExtensionId)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(ownerExtensionId);

        List<ISystemOneProvider> removed;
        lock (_gate)
        {
            ThrowIfDisposed();

            removed = _entries
                .Where(e => string.Equals(e.Owner, ownerExtensionId, StringComparison.Ordinal))
                .Select(e => e.Provider)
                .ToList();

            if (removed.Count == 0)
                return 0;

            _entries.RemoveAll(e => string.Equals(e.Owner, ownerExtensionId, StringComparison.Ordinal));
            _defaultProvider = ResolveDefaultProviderLocked();
        }

        foreach (var provider in removed)
            DisposeProviderSafe(provider);

        RaiseProvidersChanged();
        return removed.Count;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        List<ISystemOneProvider> all;
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            all = _entries.Select(e => e.Provider).ToList();
            _entries.Clear();
            _defaultProvider = null;
        }

        foreach (var provider in all)
            DisposeProviderSafe(provider);
    }

    /// <summary>
    /// 校验是否已释放；已释放则抛 <see cref="ObjectDisposedException"/>。
    /// <para>在持有 <see cref="_gate"/> 时调用可确保与 <see cref="Dispose"/> 的竞态被关闭。</para>
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SystemOneProviderManager));
    }

    /// <summary>默认 provider：env <c>SYSTEMONE_PROVIDER</c> &gt; id <c>typesafe</c> &gt; 首个 &gt; null。</summary>
    private ISystemOneProvider? ResolveDefaultProviderLocked()
    {
        var envId = SystemOneEnvironmentConfig.ResolveProvider();
        if (!string.IsNullOrWhiteSpace(envId))
        {
            foreach (var entry in _entries)
            {
                if (string.Equals(entry.Provider.Id, envId, StringComparison.OrdinalIgnoreCase))
                    return entry.Provider;
            }
        }

        foreach (var entry in _entries)
        {
            if (string.Equals(entry.Provider.Id, "typesafe", StringComparison.OrdinalIgnoreCase))
                return entry.Provider;
        }

        return _entries.Count > 0 ? _entries[0].Provider : null;
    }

    private void RaiseProvidersChanged() => ProvidersChanged?.Invoke(this, EventArgs.Empty);

    private void DisposeProviderSafe(ISystemOneProvider? provider)
    {
        if (provider is not IDisposable disposable)
            return;

        try
        {
            disposable.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "释放 SystemOne provider {ProviderId} 失败", provider.Id);
        }
    }
}
