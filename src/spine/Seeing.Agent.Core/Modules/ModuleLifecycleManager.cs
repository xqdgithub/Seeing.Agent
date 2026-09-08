using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Modules;

namespace Seeing.Agent.Core.Modules;

/// <summary>
/// 模块 Activate / Deactivate 生命周期编排 — 仅对结算启用集调用模块钩子。
/// </summary>
public sealed class ModuleLifecycleManager
{
    private readonly ModuleCatalog _catalog;
    private readonly Dictionary<string, ISeeingModule> _modulesById;
    private readonly IServiceProvider _services;
    private readonly ILogger<ModuleLifecycleManager> _logger;
    private readonly HashSet<string> _activated = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <summary>创建生命周期管理器。</summary>
    public ModuleLifecycleManager(
        ModuleCatalog catalog,
        IEnumerable<ISeeingModule> modules,
        IServiceProvider services,
        ILogger<ModuleLifecycleManager>? logger = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        ArgumentNullException.ThrowIfNull(modules);
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = logger ?? NullLogger<ModuleLifecycleManager>.Instance;

        _modulesById = new Dictionary<string, ISeeingModule>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in modules)
        {
            ArgumentNullException.ThrowIfNull(module);
            if (string.IsNullOrWhiteSpace(module.Id))
                throw new ArgumentException("ISeeingModule.Id 不能为空", nameof(modules));

            if (!_modulesById.TryAdd(module.Id, module))
            {
                throw new InvalidOperationException(
                    $"重复登记模块 id '{module.Id}'；同一 Id 只能有一个 ISeeingModule 实例。");
            }
        }
    }

    /// <summary>当前已 Activate 的模块 id。</summary>
    public IReadOnlyCollection<string> Activated
    {
        get
        {
            lock (_gate)
                return _activated.ToArray();
        }
    }

    /// <summary>模块是否处于已激活状态。</summary>
    public bool IsActivated(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (_gate)
            return _activated.Contains(id);
    }

    /// <summary>
    /// 激活目录中启用且尚未激活的模块（按 DependsOn 拓扑序）。
    /// Activate 失败时标记 unhealthy 并抛出。
    /// </summary>
    public async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        var enabled = _catalog.Enabled
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var ordered = TopologicalSort(enabled, activating: true);

        foreach (var id in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (_gate)
            {
                if (_activated.Contains(id))
                    continue;
            }

            if (!_modulesById.TryGetValue(id, out var module))
            {
                var msg = $"启用集含模块 '{id}' 但宿主未登记对应 ISeeingModule 实例。";
                _catalog.MarkUnhealthy(id, msg);
                throw new InvalidOperationException(msg);
            }

            _logger.LogInformation("Activating module {ModuleId}", id);
            try
            {
                await module.ActivateAsync(_services, cancellationToken).ConfigureAwait(false);
                await StartModuleHostedServicesAsync(id, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _catalog.MarkUnhealthy(id, ex.Message);
                _logger.LogError(ex, "Module {ModuleId} ActivateAsync failed", id);
                throw;
            }

            lock (_gate)
                _activated.Add(id);
            _catalog.ClearUnhealthy(id);
        }
    }

    /// <summary>
    /// 停用模块（对称调用 <see cref="ISeeingModule.DeactivateAsync"/>）。
    /// <paramref name="moduleIds"/> 为 null 时停用全部已激活模块；否则仅停用指定 id。
    /// 停用顺序为激活依赖的逆序。
    /// </summary>
    public async Task DeactivateAsync(
        IEnumerable<string>? moduleIds = null,
        CancellationToken cancellationToken = default)
    {
        string[] targets;
        lock (_gate)
        {
            if (moduleIds is null)
            {
                targets = _activated.ToArray();
            }
            else
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var id in moduleIds)
                {
                    if (!string.IsNullOrWhiteSpace(id) && _activated.Contains(id))
                        set.Add(id);
                }

                targets = set.ToArray();
            }
        }

        if (targets.Length == 0)
            return;

        var ordered = TopologicalSort(targets, activating: false);

        foreach (var id in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_modulesById.TryGetValue(id, out var module))
            {
                lock (_gate)
                    _activated.Remove(id);
                continue;
            }

            _logger.LogInformation("Deactivating module {ModuleId}", id);
            await StopModuleHostedServicesAsync(id, cancellationToken).ConfigureAwait(false);
            await module.DeactivateAsync(_services, cancellationToken).ConfigureAwait(false);

            lock (_gate)
                _activated.Remove(id);
        }
    }

    private async Task StartModuleHostedServicesAsync(string moduleId, CancellationToken cancellationToken)
    {
        foreach (var hosted in ResolveModuleHostedServices(moduleId))
        {
            if (hosted.IsRunning)
                continue;

            _logger.LogInformation(
                "Starting module hosted service {Service} for {ModuleId}",
                hosted.GetType().Name,
                moduleId);
            await hosted.StartAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task StopModuleHostedServicesAsync(string moduleId, CancellationToken cancellationToken)
    {
        foreach (var hosted in ResolveModuleHostedServices(moduleId))
        {
            if (!hosted.IsRunning)
                continue;

            _logger.LogInformation(
                "Stopping module hosted service {Service} for {ModuleId}",
                hosted.GetType().Name,
                moduleId);
            await hosted.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private IEnumerable<IModuleHostedService> ResolveModuleHostedServices(string moduleId)
    {
        IEnumerable<IModuleHostedService> all;
        try
        {
            all = _services.GetServices<IModuleHostedService>();
        }
        catch (ObjectDisposedException)
        {
            yield break;
        }

        foreach (var hosted in all)
        {
            if (hosted is null)
                continue;
            if (!string.Equals(hosted.ModuleId, moduleId, StringComparison.OrdinalIgnoreCase))
                continue;
            yield return hosted;
        }
    }

    private List<string> TopologicalSort(IReadOnlyList<string> ids, bool activating)
    {
        var idSet = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        void Visit(string id)
        {
            if (visited.Contains(id) || !idSet.Contains(id))
                return;
            if (!visiting.Add(id))
                throw new InvalidOperationException($"模块依赖成环，涉及 '{id}'。");

            if (_catalog.TryGet(id, out var descriptor))
            {
                foreach (var dep in descriptor.DependsOn)
                {
                    if (string.IsNullOrWhiteSpace(dep) || !idSet.Contains(dep))
                        continue;
                    Visit(dep);
                }
            }
            else if (_modulesById.TryGetValue(id, out var module))
            {
                foreach (var dep in module.DependsOn)
                {
                    if (string.IsNullOrWhiteSpace(dep) || !idSet.Contains(dep))
                        continue;
                    Visit(dep);
                }
            }

            visiting.Remove(id);
            visited.Add(id);
            result.Add(id);
        }

        foreach (var id in ids.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            Visit(id);

        if (!activating)
            result.Reverse();

        return result;
    }
}
