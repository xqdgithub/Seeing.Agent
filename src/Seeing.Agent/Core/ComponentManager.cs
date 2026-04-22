using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Commands;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Extensions;
using Seeing.Agent.Hooks;
using Seeing.Agent.MCP;
using Seeing.Agent.Rules;
using Seeing.Agent.Skills;
using Seeing.Agent.Tools;
using System.Collections.Concurrent;

namespace Seeing.Agent.Core;

/// <summary>
/// 组件管理器 - 统一管理 Skills/MCP/Plugins/Rules 的发现和加载
/// <para>
/// 配置层级：
/// - 用户级：~/.seeing/（基础配置）
/// - 项目级：./.seeing/（覆盖同名）
/// - IConfiguration：运行时覆盖
/// </para>
/// </summary>
public class ComponentManager : IComponentManager
{
    private readonly IServiceProvider _services;
    private readonly ILogger<ComponentManager> _logger;
    private readonly ConcurrentDictionary<ComponentType, IComponentLoader> _loaders = new();
    private readonly ConcurrentDictionary<ComponentType, ComponentLoadResult> _loadStatus = new();

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
        _loaders[ComponentType.Skill] = new SkillLoader();
        _loaders[ComponentType.Mcp] = new McpLoader();
        _loaders[ComponentType.Plugin] = new PluginLoader();
        _loaders[ComponentType.Rule] = new RuleLoader();
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
    public IReadOnlyDictionary<ComponentType, ComponentLoadResult> GetLoadStatus() => _loadStatus;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ComponentLoadResult>> LoadAllAsync(
        string workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("开始加载所有组件，工作区: {Workspace}", workspaceRoot);

        var results = new List<ComponentLoadResult>();

        // 按顺序加载：Skill → MCP → Plugin → Rule → 自定义
        var order = new[] { ComponentType.Skill, ComponentType.Mcp, ComponentType.Plugin, ComponentType.Rule };

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
        ComponentType type,
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
            var result = await loader.LoadAsync(_services, workspaceRoot, cancellationToken);
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
}

#region 内置加载器

/// <summary>技能加载器</summary>
internal class SkillLoader : IComponentLoader
{
    public ComponentType Type => ComponentType.Skill;

    public async Task<ComponentLoadResult> LoadAsync(
        IServiceProvider services,
        string workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        var skillManager = services.GetRequiredService<SkillManager>();
        var options = services.GetService<IOptions<SeeingAgentOptions>>();
        var loggerFactory = services.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger<SkillLoader>();

        skillManager.ResetSearchDirectoriesToDefault();

        // 用户级 ~/.seeing/skills
        var userSkills = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".seeing", "skills");
        AddIfExists(skillManager, userSkills);

        // 配置中的路径
        if (options?.Value?.Skills?.Paths != null)
        {
            foreach (var p in options.Value.Skills.Paths)
            {
                if (!string.IsNullOrWhiteSpace(p))
                    AddIfExists(skillManager, ExpandPath(p.Trim(), workspaceRoot));
            }
        }

        // 项目级目录
        AddIfExists(skillManager, Path.Combine(workspaceRoot, ".seeing", "skills"));
        AddIfExists(skillManager, Path.Combine(workspaceRoot, ".agents", "skills"));
        AddIfExists(skillManager, Path.Combine(workspaceRoot, "skills"));

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
}

/// <summary>MCP 加载器</summary>
internal class McpLoader : IComponentLoader
{
    public ComponentType Type => ComponentType.Mcp;

    public async Task<ComponentLoadResult> LoadAsync(
        IServiceProvider services,
        string workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        var mcpManager = services.GetRequiredService<McpClientManager>();
        var toolInvoker = services.GetRequiredService<ToolInvoker>();
        var loggerFactory = services.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger<McpLoader>();

        var configs = McpConfigLoader.LoadDefault(workspaceRoot, logger);
        var connected = new List<string>();

        foreach (var config in configs)
        {
            try
            {
                await mcpManager.ConnectAsync(config, cancellationToken);
                connected.Add(config.Name);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "MCP {Name} 连接失败", config.Name);
            }
        }

        // 注册工具
        foreach (var tool in mcpManager.GetToolsAsITools())
            toolInvoker.RegisterTool(tool);

        return new ComponentLoadResult
        {
            Type = Type,
            Success = true,
            Count = mcpManager.GetTools().Count,
            Details = connected
        };
    }
}

/// <summary>插件加载器</summary>
internal class PluginLoader : IComponentLoader
{
    public ComponentType Type => ComponentType.Plugin;

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

        var context = new ExtensionContext
        {
            Services = services,
            Configuration = configuration,
            Directory = Directory.GetCurrentDirectory(),
            WorkspaceRoot = workspaceRoot,
            HookManager = services.GetRequiredService<HookManager>(),
            ToolInvoker = services.GetRequiredService<ToolInvoker>(),
            RuleEngine = services.GetRequiredService<RuleEngine>(),
            SkillManager = services.GetRequiredService<SkillManager>(),
            AgentRegistry = services.GetRequiredService<IAgentRegistry>(),
            McpClientManager = services.GetRequiredService<McpClientManager>(),
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

/// <summary>规则加载器</summary>
internal class RuleLoader : IComponentLoader
{
    public ComponentType Type => ComponentType.Rule;

    public async Task<ComponentLoadResult> LoadAsync(
        IServiceProvider services,
        string workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        var ruleEngine = services.GetRequiredService<RuleEngine>();
        var loggerFactory = services.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger<RuleLoader>();

        // 用户级 ~/.seeing/rules
        var userRulesDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".seeing", "rules");

        // 项目级目录
        var projectRulesDirs = new[]
        {
            Path.Combine(workspaceRoot, ".seeing", "rules"),
            Path.Combine(workspaceRoot, ".agents", "rules"),
            Path.Combine(workspaceRoot, "rules")
        };

        var loadedFiles = new List<string>();

        // 加载规则文件（用户级为基础，项目级覆盖）
        foreach (var dir in new[] { userRulesDir }.Concat(projectRulesDirs))
        {
            if (Directory.Exists(dir))
            {
                var files = Directory.GetFiles(dir, "*.md");
                foreach (var file in files)
                {
                    try
                    {
                        var content = await File.ReadAllTextAsync(file, cancellationToken);
                        var rules = ParseRulesFromMarkdown(content);
                        foreach (var rule in rules)
                        {
                            ruleEngine.AddRule(rule);
                            loadedFiles.Add(Path.GetFileNameWithoutExtension(file));
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "规则文件加载失败: {File}", file);
                    }
                }
            }
        }

        return new ComponentLoadResult
        {
            Type = Type,
            Success = true,
            Count = loadedFiles.Count,
            Details = loadedFiles
        };
    }

    private static List<PermissionRule> ParseRulesFromMarkdown(string content)
    {
        // 简化实现：从 Markdown 解析规则
        // 实际实现可以更复杂
        var rules = new List<PermissionRule>();
        // TODO: 实现完整的 Markdown 规则解析
        return rules;
    }
}

#endregion