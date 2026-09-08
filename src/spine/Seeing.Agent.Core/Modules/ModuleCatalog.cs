using Seeing.Agent.Abstractions.Modules;

namespace Seeing.Agent.Core.Modules;

/// <summary>
/// 进程级模块目录 — 持有宿主 available 与结算后的 enabled / unhealthy。
/// </summary>
public sealed class ModuleCatalog : IModuleCatalog
{
    private readonly object _gate = new();
    private Dictionary<string, ModuleDescriptor> _available =
        new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _enabled = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _boundSeams =
        new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string?> _unhealthy =
        new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public IReadOnlyCollection<ModuleDescriptor> Available
    {
        get
        {
            lock (_gate)
                return _available.Values.ToArray();
        }
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> Enabled
    {
        get
        {
            lock (_gate)
                return _enabled.ToArray();
        }
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string?> Unhealthy
    {
        get
        {
            lock (_gate)
                return new Dictionary<string, string?>(_unhealthy, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>独占 seam 绑定（seam 名 → 提供方模块 id）。</summary>
    public IReadOnlyDictionary<string, string> BoundSeams
    {
        get
        {
            lock (_gate)
                return new Dictionary<string, string>(_boundSeams, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <inheritdoc />
    public bool IsEnabled(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (_gate)
            return _enabled.Contains(id);
    }

    /// <inheritdoc />
    public bool IsAvailable(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (_gate)
            return _available.ContainsKey(id);
    }

    /// <summary>尝试按 id 取模块描述符。</summary>
    public bool TryGet(string id, out ModuleDescriptor descriptor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (_gate)
            return _available.TryGetValue(id, out descriptor!);
    }

    /// <summary>标记模块不健康（Activate 失败等）；原因可为 null。</summary>
    public void MarkUnhealthy(string id, string? reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (_gate)
            _unhealthy[id] = reason;
    }

    /// <summary>清除不健康标记。</summary>
    public void ClearUnhealthy(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (_gate)
            _unhealthy.Remove(id);
    }

    /// <summary>由结算引擎写入宿主目录快照。</summary>
    internal void ReplaceAvailable(IEnumerable<ModuleDescriptor> available)
    {
        ArgumentNullException.ThrowIfNull(available);
        var next = new Dictionary<string, ModuleDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (var descriptor in available)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            if (string.IsNullOrWhiteSpace(descriptor.Id))
                throw new ArgumentException("ModuleDescriptor.Id 不能为空", nameof(available));
            next[descriptor.Id] = descriptor;
        }

        lock (_gate)
            _available = next;
    }

    /// <summary>由结算引擎写入启用集（替换，非整段追加）。</summary>
    internal void ReplaceEnabled(IEnumerable<string> enabled)
    {
        ArgumentNullException.ThrowIfNull(enabled);
        var next = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in enabled)
        {
            if (!string.IsNullOrWhiteSpace(id))
                next.Add(id);
        }

        lock (_gate)
            _enabled = next;
    }

    /// <summary>由结算引擎写入独占 seam 绑定。</summary>
    internal void ReplaceBoundSeams(IReadOnlyDictionary<string, string> boundSeams)
    {
        ArgumentNullException.ThrowIfNull(boundSeams);
        var next = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (seam, moduleId) in boundSeams)
        {
            if (string.IsNullOrWhiteSpace(seam) || string.IsNullOrWhiteSpace(moduleId))
                continue;
            next[seam.Trim()] = moduleId.Trim();
        }

        lock (_gate)
            _boundSeams = next;
    }
}
