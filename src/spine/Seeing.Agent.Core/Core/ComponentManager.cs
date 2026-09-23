using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Components;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Core.Commands;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Agent.Abstractions.Commands;
using System.Collections.Concurrent;

namespace Seeing.Agent.Core;

/// <summary>
/// 组件管理器 - 统一调度已登记的 <see cref="IComponentLoader"/>（Skills/MCP 等由能力模块登记）。
/// </summary>
public class ComponentManager : IComponentManager, IReloadHandler
{
    /// <inheritdoc/>
    public string ComponentId => "components";

    /// <inheritdoc/>
    public IReadOnlyList<Type> ChangeTypes { get; } = new[] { typeof(WorkspaceChange), typeof(ConfigChange) };

    private readonly IServiceProvider _services;
    private readonly ILogger<ComponentManager> _logger;
    private readonly ConcurrentDictionary<string, IComponentLoader> _loaders = new();
    private readonly ConcurrentDictionary<string, ComponentLoadResult> _loadStatus = new();

    /// <summary>
    /// 构造组件管理器，登记可选的组件加载器集合。
    /// </summary>
    public ComponentManager(
        IServiceProvider services,
        ILogger<ComponentManager> logger,
        IEnumerable<IComponentLoader>? loaders = null)
    {
        _services = services;
        _logger = logger;

        if (loaders is null)
            return;

        foreach (var loader in loaders)
        {
            _loaders[loader.Type] = loader;
            _logger.LogInformation("登记组件加载器: {Type}", loader.Type);
        }
    }

    /// <inheritdoc/>
    public void RegisterLoader(IComponentLoader loader)
    {
        _loaders[loader.Type] = loader;
        _logger.LogInformation("注册组件加载器: {Type}", loader.Type);
    }

    /// <inheritdoc/>
    public IReadOnlyList<IComponentLoader> GetLoaders() => _loaders.Values.ToList();

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, ComponentLoadResult> GetLoadStatus() => _loadStatus;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ComponentLoadResult>> LoadAllAsync(
        string workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("开始加载所有组件，工作区: {Workspace}", workspaceRoot);

        var results = new List<ComponentLoadResult>();

        // 优先顺序：Skill → MCP → 其余
        var order = new[] { "Skill", "Mcp" };

        foreach (var type in order)
        {
            if (_loaders.TryGetValue(type, out _))
            {
                var result = await LoadAsync(type, workspaceRoot, cancellationToken);
                results.Add(result);
            }
        }

        var customTypes = _loaders.Keys.Except(order).ToList();
        foreach (var type in customTypes)
        {
            var result = await LoadAsync(type, workspaceRoot, cancellationToken);
            results.Add(result);
        }

        var successCount = results.Count(r => r.Success);
        var totalCount = results.Sum(r => r.Count);
        _logger.LogInformation("组件加载完成: {Success}/{Total} 类型成功，共加载 {Count} 个组件",
            successCount, results.Count, totalCount);

        return results;
    }

    /// <inheritdoc/>
    public async Task<ComponentLoadResult> LoadAsync(
        string type,
        string workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        if (!_loaders.TryGetValue(type, out var loader))
        {
            return new ComponentLoadResult
            {
                Type = type,
                Success = false,
                Error = $"未注册 {type} 类型的加载器"
            };
        }

        try
        {
            var previouslyLoaded = _loadStatus.TryGetValue(type, out var previous) && previous.Success;
            var result = previouslyLoaded
                ? await loader.ReloadAsync(_services, workspaceRoot, cancellationToken)
                : await loader.LoadAsync(_services, workspaceRoot, cancellationToken);
            _loadStatus[type] = result;

            if (result.Success)
                _logger.LogInformation("{Type} 加载成功: {Count} 个", type, result.Count);
            else
                _logger.LogWarning("{Type} 加载失败: {Error}", type, result.Error);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Type} 加载异常", type);

            var result = new ComponentLoadResult
            {
                Type = type,
                Success = false,
                Error = ex.Message
            };
            _loadStatus[type] = result;
            return result;
        }
    }

    /// <inheritdoc/>
    public async Task ReloadAsync(IReloadSignal change, CancellationToken ct = default)
    {
        var workspaceRoot = _services.GetRequiredService<IWorkspaceProvider>().GetProjectRoot();

        if (change is WorkspaceChange)
        {
            await LoadAllAsync(workspaceRoot, ct);
        }
        else if (change is ConfigChange cfg)
        {
            if (cfg.ChangedSections.Count == 0)
            {
                await LoadAllAsync(workspaceRoot, ct);
                return;
            }

            foreach (var section in cfg.ChangedSections)
            {
                if (section == "Skills") await LoadAsync("Skill", workspaceRoot, ct);
                else if (section == "Mcp") await LoadAsync("Mcp", workspaceRoot, ct);
            }
        }
    }
}
