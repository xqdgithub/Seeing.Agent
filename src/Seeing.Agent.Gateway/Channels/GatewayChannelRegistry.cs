using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Configuration;
using Seeing.Agent.Extensions;
using Seeing.Gateway.Configuration;
using Seeing.Gateway.Plugins;
using Seeing.Gateway.WeCom;
using Seeing.Gateway.QQ;

namespace Seeing.Agent.Gateway.Channels;

/// <summary>
/// Gateway Channel 插件注册表（内置 + DLL 扫描）
/// </summary>
public sealed class GatewayChannelRegistry
{
    public const string BuiltinWeComSpec = "builtin:wecom";
    public const string BuiltinQQSpec = "builtin:qq";

    private readonly ILogger<GatewayChannelRegistry> _logger;
    private readonly ExtensionLoader _extensionLoader;
    private readonly UnifiedConfigManager _configManager;
    private IReadOnlyList<GatewayChannelTypeInfo> _types = Array.Empty<GatewayChannelTypeInfo>();

    public GatewayChannelRegistry(
        ILogger<GatewayChannelRegistry> logger,
        ExtensionLoader extensionLoader,
        UnifiedConfigManager configManager)
    {
        _logger = logger;
        _extensionLoader = extensionLoader;
        _configManager = configManager;
    }

    public IReadOnlyList<GatewayChannelTypeInfo> Types => _types;

    public void Reload(string workspaceRoot)
    {
        var discovered = new Dictionary<string, GatewayChannelTypeInfo>(StringComparer.OrdinalIgnoreCase);
        var options = _configManager.GetSection<GatewayClientsOptions>("GatewayClients");

        RegisterBuiltinPlugins(discovered);
        RegisterConfiguredPlugins(discovered, options, workspaceRoot);
        ScanPluginDirectories(discovered, workspaceRoot);

        _types = discovered.Values
            .OrderByDescending(t => t.IsBuiltin)
            .ThenBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _logger.LogInformation("Gateway Channel 注册表已刷新，共 {Count} 个类型", _types.Count);
    }

    public GatewayChannelTypeInfo? GetTypeInfo(string channelId) =>
        _types.FirstOrDefault(t => t.ChannelId.Equals(channelId, StringComparison.OrdinalIgnoreCase));

    public GatewayChannelTypeInfo? GetTypeInfoBySpec(string pluginSpec) =>
        _types.FirstOrDefault(t => t.PluginSpec.Equals(pluginSpec, StringComparison.OrdinalIgnoreCase));

    public IGatewayChannelPlugin? LoadPlugin(GatewayChannelTypeInfo typeInfo)
    {
        try
        {
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(typeInfo.AssemblyPath));
            var pluginType = assembly.GetTypes()
                .FirstOrDefault(t => typeof(IGatewayChannelPlugin).IsAssignableFrom(t)
                                     && !t.IsInterface
                                     && !t.IsAbstract);

            if (pluginType == null)
            {
                _logger.LogWarning("程序集中未找到 IGatewayChannelPlugin: {Path}", typeInfo.AssemblyPath);
                return null;
            }

            return (IGatewayChannelPlugin)Activator.CreateInstance(pluginType)!;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载 Gateway Channel 插件失败: {Path}", typeInfo.AssemblyPath);
            return null;
        }
    }

    private void RegisterBuiltinPlugins(Dictionary<string, GatewayChannelTypeInfo> discovered)
    {
        RegisterPluginInstance(discovered, new WeComChannelPlugin(), ResolveBuiltinAssemblyPath<WeComChannelPlugin>(), BuiltinWeComSpec);
        RegisterPluginInstance(discovered, new QQChannelPlugin(), ResolveBuiltinAssemblyPath<QQChannelPlugin>(), BuiltinQQSpec);
    }

    private void RegisterConfiguredPlugins(
        Dictionary<string, GatewayChannelTypeInfo> discovered,
        GatewayClientsOptions options,
        string workspaceRoot)
    {
        foreach (var plugin in options.Plugins)
        {
            if (string.IsNullOrWhiteSpace(plugin.Spec))
                continue;

            if (plugin.Spec.StartsWith("builtin:", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var path = ResolvePluginPath(plugin.Spec, workspaceRoot);
                RegisterFromAssembly(discovered, path, plugin.Spec);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "跳过无效 Gateway Client 插件: {Spec}", plugin.Spec);
            }
        }
    }

    private void ScanPluginDirectories(Dictionary<string, GatewayChannelTypeInfo> discovered, string workspaceRoot)
    {
        foreach (var directory in GetPluginDirectories(workspaceRoot))
        {
            if (!Directory.Exists(directory))
                continue;

            foreach (var dll in Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    RegisterFromAssembly(discovered, dll, $"file://{dll}");
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "跳过无效插件 DLL: {Dll}", dll);
                }
            }
        }
    }

    private void RegisterFromAssembly(Dictionary<string, GatewayChannelTypeInfo> discovered, string assemblyPath, string pluginSpec)
    {
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));
        foreach (var pluginType in assembly.GetTypes()
                     .Where(t => typeof(IGatewayChannelPlugin).IsAssignableFrom(t)
                                 && !t.IsInterface
                                 && !t.IsAbstract))
        {
            var plugin = (IGatewayChannelPlugin)Activator.CreateInstance(pluginType)!;
            RegisterPluginInstance(discovered, plugin, assemblyPath, pluginSpec);
        }
    }

    private void RegisterPluginInstance(
        Dictionary<string, GatewayChannelTypeInfo> discovered,
        IGatewayChannelPlugin plugin,
        string assemblyPath,
        string pluginSpec)
    {
        var fields = plugin.GetConfigSchema() ?? OptionsSchemaBuilder.FromType(plugin.OptionsType);
        var info = new GatewayChannelTypeInfo(
            plugin.ChannelId,
            plugin.DisplayName,
            plugin.Description,
            plugin.IsBuiltin,
            pluginSpec,
            assemblyPath,
            plugin.OptionsSectionName,
            plugin.OptionsType,
            fields,
            plugin.ConfigFormComponentType);

        discovered[plugin.ChannelId] = info;
    }

    private static IEnumerable<string> GetPluginDirectories(string workspaceRoot)
    {
        var userDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".seeing",
            "gateway-channels");
        var projectDir = Path.Combine(workspaceRoot, ".seeing", "gateway-channels");
        yield return projectDir;
        yield return userDir;
    }

    private string ResolvePluginPath(string spec, string workspaceRoot)
    {
        if (spec.StartsWith("builtin:", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"内置插件不应通过文件路径解析: {spec}");

        return _extensionLoader.ResolveTarget(spec).GetAwaiter().GetResult();
    }

    private static string ResolveBuiltinAssemblyPath<T>() =>
        Path.GetFullPath(typeof(T).Assembly.Location);
}
