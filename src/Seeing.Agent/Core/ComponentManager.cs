using Seeing.Agent.Abstractions.Mcp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Commands;
using Seeing.Agent.Configuration;
using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Extensions;using Seeing.Agent.Core.Permission;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Extensions;
using Seeing.Agent.MCP;
using Seeing.Agent.Skills;
using Seeing.Agent.Tools;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Abstractions.Components;
using Seeing.Agent.Abstractions.Skills;
using System.Collections.Concurrent;

namespace Seeing.Agent.Core;

/// <summary>
/// 组件管理器 - 统一管理 Skills/MCP/Plugins/Rules 的发现和加载
/// <para>
    /// 配置层级：
    /// - 用户级：~/.seeing/（基础配置）
    /// - 项目级：./.seeing/（覆盖同名）
    /// </para>
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

    public ComponentManager(IServiceProvider services, ILogger<ComponentManager> logger)
    {
        _services = services;
        _logger = logger;

        // 注册内置加载器
        RegisterBuiltInLoaders();
    }

    /// <summary>注册内置加载器</summary>
    private void RegisterBuiltInLoaders()
    {
        _loaders["Skill"] = new SkillLoader();
        _loaders["Mcp"] = new McpLoader();
        _loaders["Plugin"] = new PluginLoader();
        // Rule loader removed - rules are now managed through PermissionService
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

        // 按顺序加载：Skill → MCP → Plugin → Rule → 自定义
        var order = new[] { "Skill", "Mcp", "Plugin", "Rule" };

        foreach (var type in order)
        {
            if (_loaders.TryGetValue(type, out var loader))
            {
                var result = await LoadAsync(type, workspaceRoot, cancellationToken);
                results.Add(result);
            }
        }

        // 加载自定义组件
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
            // 已成功加载过的类型走重载路径（清理旧状态后重新发现），否则走首次加载路径
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
        var workspaceRoot = _services.GetRequiredService<IWorkspaceProvider>().WorkspaceRoot;

        if (change is WorkspaceChange)
        {
            // 工作区切换：全量重载（各 Loader 走 ReloadAsync 清理旧状态后重新发现）
            await LoadAllAsync(workspaceRoot, ct);
        }
        else if (change is ConfigChange cfg)
        {
            // 配置变更：空节数组表示全量重载
            if (cfg.ChangedSections.Count == 0)
            {
                await LoadAllAsync(workspaceRoot, ct);
                return;
            }

            // 按变更配置节分发到对应 Loader
            foreach (var section in cfg.ChangedSections)
            {
                if (section == "Skills") await LoadAsync("Skill", workspaceRoot, ct);
                else if (section == "Mcp") await LoadAsync("Mcp", workspaceRoot, ct);
                else if (section is "Plugins" or "PluginEnabled") await LoadAsync("Plugin", workspaceRoot, ct);
            }
        }
    }
}

#region 内置加载器

/// <summary>技能加载器</summary>
internal class SkillLoader : IComponentLoader
{
    public string Type => "Skill";

    public async Task<ComponentLoadResult> LoadAsync(
        IServiceProvider services,
        string workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        var skillManager = services.GetRequiredService<SkillManager>();
        var options = services.GetService<IOptions<SeeingAgentOptions>>();
        var loggerFactory = services.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger<SkillLoader>();
        var workspaceProvider = services.GetService<IWorkspaceProvider>() ?? new WorkspaceProvider(workspaceRoot);

        skillManager.ResetSearchDirectoriesToDefault();

        // 用户级 ~/.seeing/skills
        AddIfExists(skillManager, Path.Combine(workspaceProvider.UserSeeingDirectory, "skills"));

        // 配置中的额外路径
        if (options?.Value?.Skills?.Paths != null)
        {
            foreach (var p in options.Value.Skills.Paths)
            {
                if (!string.IsNullOrWhiteSpace(p))
                    AddIfExists(skillManager, ExpandPath(p.Trim(), workspaceProvider.WorkspaceRoot));
            }
        }

        await skillManager.DiscoverSkillsAsync(cancellationToken);

        return new ComponentLoadResult
        {
            Type = Type,
            Success = true,
            Count = skillManager.GetAllSkillInfos().Count,
            Details = skillManager.GetAllSkillInfos().Keys.ToList()
        };
    }

    private static void AddIfExists(SkillManager manager, string dir)
    {
        if (Directory.Exists(dir))
            manager.AddSearchDirectory(dir);
    }

    private static string ExpandPath(string path, string workspaceRoot)
    {
        if (path.StartsWith("~"))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.GetFullPath(Path.Combine(home, path.Substring(1).TrimStart('/', '\\')));
        }
        return Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(workspaceRoot, path));
    }

    /// <inheritdoc/>
    public async Task<ComponentLoadResult> ReloadAsync(
        IServiceProvider services,
        string workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        // 先清空旧技能（已删除的技能信息也会被移除），再重挂载目录并重新发现
        var skillManager = services.GetRequiredService<SkillManager>();
        skillManager.ClearSkillInfos();
        return await LoadAsync(services, workspaceRoot, cancellationToken);
    }
}

/// <summary>MCP 加载器</summary>
internal class McpLoader : IComponentLoader
{
    public string Type => "Mcp";

    public async Task<ComponentLoadResult> LoadAsync(
        IServiceProvider services,
        string workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        var mcpManager = services.GetRequiredService<McpClientManager>();
        var loggerFactory = services.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger<McpLoader>();
        var workspaceProvider = services.GetService<IWorkspaceProvider>() ?? new WorkspaceProvider(workspaceRoot);

        // 加载配置（不阻塞）
        var configs = McpConfigLoader.LoadDefault(workspaceProvider, logger);

        // 转换为字典格式
        var configDict = new Dictionary<string, McpServerConfig>();
        foreach (var config in configs)
        {
            if (!string.IsNullOrEmpty(config.Name))
                configDict[config.Name] = config;
        }

        // 非阻塞初始化（后台启动连接）
        await mcpManager.InitializeAsync(configDict, cancellationToken);

        // 注意：工具注册已由 McpClientManager 内部处理（通过 McpToolRegistry）
        // 不需要在此手动注册

        return new ComponentLoadResult
        {
            Type = Type,
            Success = true,
            Count = configs.Count,
            Details = configs.Select(c => c.Name).ToList()
        };
    }

    /// <inheritdoc/>
    public async Task<ComponentLoadResult> ReloadAsync(
        IServiceProvider services,
        string workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        var mcpManager = services.GetRequiredService<McpClientManager>();
        var loggerFactory = services.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger<McpLoader>();
        var workspaceProvider = services.GetService<IWorkspaceProvider>() ?? new WorkspaceProvider(workspaceRoot);

        // 重新加载配置（读取磁盘最新配置）
        var configs = McpConfigLoader.LoadDefault(workspaceProvider, logger);

        // 转换为字典格式（跳过空名称，与 LoadAsync 保持一致）
        var configDict = new Dictionary<string, McpServerConfig>();
        foreach (var config in configs)
        {
            if (!string.IsNullOrEmpty(config.Name))
                configDict[config.Name] = config;
        }

        // 重置全部连接后重新初始化（幂等，清理旧状态再按最新配置加载）
        await mcpManager.ResetAllAsync(cancellationToken);
        await mcpManager.InitializeAsync(configDict, cancellationToken);

        return new ComponentLoadResult
        {
            Type = Type,
            Success = true,
            Count = configs.Count,
            Details = configs.Select(c => c.Name).ToList()
        };
    }
}

/// <summary>插件加载器</summary>
internal class PluginLoader : IComponentLoader
{
    public string Type => "Plugin";

    public async Task<ComponentLoadResult> LoadAsync(
        IServiceProvider services,
        string workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        var extensionManager = services.GetRequiredService<ExtensionManager>();
        var options = services.GetService<IOptions<SeeingAgentOptions>>();
        var configuration = services.GetRequiredService<IConfiguration>();
        var loggerFactory = services.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger<PluginLoader>();
        var workspaceProvider = services.GetService<IWorkspaceProvider>() ?? new WorkspaceProvider(workspaceRoot);

        var context = new ExtensionContext
        {
            Services = services,
            Configuration = configuration,
            Directory = workspaceProvider.WorkspaceRoot,
            WorkspaceRoot = workspaceProvider.WorkspaceRoot,
            HookManager = services.GetRequiredService<HookManager>(),
            ToolManager = services.GetRequiredService<ToolManager>(),
            PermissionService = services.GetRequiredService<IPermissionService>(),
            AgentRegistry = services.GetRequiredService<IAgentRegistry>(),
            McpClientManager = services.GetRequiredService<McpClientManager>(),
            SkillManager = services.GetRequiredService<ISkillManager>(),
            CommandRegistry = services.GetRequiredService<ICommandRegistry>()
        };

        var pluginSpecs = options?.Value?.Plugins ?? new List<PluginSpec>();
        var enabledOverrides = options?.Value?.PluginEnabled ?? new Dictionary<string, bool>();

        // 自动查找内置插件
        if (pluginSpecs.Count == 0)
        {
            var pluginsDll = FindPluginsAssembly();
            if (pluginsDll != null)
            {
                logger.LogInformation("自动加载内置插件: {Path}", pluginsDll);
                pluginSpecs = new List<PluginSpec> { new PluginSpec { Spec = pluginsDll } };
            }
        }

        if (pluginSpecs.Count == 0)
        {
            return new ComponentLoadResult
            {
                Type = Type,
                Success = true,
                Count = 0,
                Details = new List<string> { "无插件配置" }
            };
        }

        await extensionManager.InitializeAsync(pluginSpecs, enabledOverrides, context, cancellationToken);

        return new ComponentLoadResult
        {
            Type = Type,
            Success = true,
            Count = extensionManager.GetAll().Count,
            Details = extensionManager.GetAll().Select(e => e.Id).ToList()
        };
    }

    /// <inheritdoc/>
    public async Task<ComponentLoadResult> ReloadAsync(
        IServiceProvider services,
        string workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        // 先卸载全部插件（释放资源并清空注册状态），再按最新配置重新加载
        var extensionManager = services.GetRequiredService<ExtensionManager>();
        await extensionManager.DisposeAllAsync();
        return await LoadAsync(services, workspaceRoot, cancellationToken);
    }

    private static string? FindPluginsAssembly()
    {
        var fileName = "Seeing.Agent.Plugins.dll";
        var candidates = new[] { AppContext.BaseDirectory, AppDomain.CurrentDomain.BaseDirectory };

        foreach (var dir in candidates)
        {
            var path = Path.Combine(dir, fileName);
            if (File.Exists(path))
                return path;
        }
        return null;
    }
}

#endregion