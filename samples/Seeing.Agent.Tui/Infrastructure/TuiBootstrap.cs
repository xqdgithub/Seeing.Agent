using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Commands;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Extensions;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Llm;
using Seeing.Agent.MCP;
using Seeing.Agent.Rules;
using Seeing.Agent.Skills;
using Seeing.Agent.Tui.Core;
using Seeing.Agent.Tui.Infrastructure;
using Seeing.Agent.Tools;

namespace Seeing.Agent.Tui.Infrastructure;

/// <summary>
/// TUI 启动引导器 - 封装初始化逻辑
/// </summary>
public static class TuiBootstrap
{
    /// <summary>
    /// 初始化工作区
    /// </summary>
    public static async Task InitializeWorkspaceAsync(IServiceProvider provider, ILogger logger)
    {
        var state = provider.GetRequiredService<TuiState>();
        var registry = provider.GetRequiredService<IAgentRegistry>();

        // 加载规则
        var (rulesText, sources) = RulesMarkdownLoader.Load(state.WorkspaceRoot);
        state.RulesMarkdown = rulesText;
        state.RulesSources = sources;

        // 合并权限规则
        provider.GetRequiredService<RuleEngine>().MergeRules(PermissionRulesFile.TryLoad(state.WorkspaceRoot));

        // 加载技能
        await SkillPathBootstrap.ApplyAsync(
            provider.GetRequiredService<SkillManager>(),
            provider.GetRequiredService<IOptions<SeeingAgentOptions>>(),
            state.WorkspaceRoot);

        // 加载扩展（在 MCP 之前）
        await LoadExtensionsAsync(provider, state, logger);

        // 连接MCP
        await ConnectMcpAsync(provider, state, logger);

        await ApplyRegistryDefaultAgentAsync(registry, state);

        await TuiRuntimeSnapshot.RefreshAsync(provider, state);
    }

    private static async Task ApplyRegistryDefaultAgentAsync(IAgentRegistry registry, TuiState state)
    {
        try
        {
            state.CurrentAgentKey = await registry.GetDefaultAgentNameAsync();
        }
        catch (InvalidOperationException)
        {
            var primaries = await registry.GetPrimaryAgentsAsync();
            if (primaries.Count == 0)
                throw new InvalidOperationException(
                    "注册中心中没有任何可用的主 Agent。请在 SeeingAgent:Agents 中配置，或确保扩展（如 Seeing.Agent.Plugins）已成功加载。");

            state.CurrentAgentKey = primaries[0].Name;
        }
    }

    private static async Task LoadExtensionsAsync(IServiceProvider provider, TuiState state, ILogger logger)
    {
        var extensionManager = provider.GetRequiredService<ExtensionManager>();

        // 构建扩展上下文
        var context = new ExtensionContext
        {
            Services = provider,
            Configuration = provider.GetRequiredService<IConfiguration>(),
            Directory = Directory.GetCurrentDirectory(),
            WorkspaceRoot = state.WorkspaceRoot,
            HookManager = provider.GetRequiredService<IHookManager>(),
            ToolInvoker = provider.GetRequiredService<ToolInvoker>(),
            RuleEngine = provider.GetRequiredService<RuleEngine>(),
            SkillManager = provider.GetRequiredService<SkillManager>(),
            AgentRegistry = provider.GetRequiredService<IAgentRegistry>(),
            McpClientManager = provider.GetRequiredService<McpClientManager>(),
            CommandRegistry = provider.GetRequiredService<ICommandRegistry>()
        };

        // 加载扩展配置
        var pluginSpecs = ExtensionConfigLoader.LoadDefault(state.WorkspaceRoot, logger).ToList();
        var enabledOverrides = ExtensionConfigLoader.LoadDefaultEnabledOverrides(state.WorkspaceRoot, logger);

        // 合并 appsettings / 环境变量中的配置
        var optsPlugins = provider.GetRequiredService<IOptions<SeeingAgentOptions>>().Value.Plugins;
        foreach (var p in optsPlugins)
        {
            if (p is { Spec: not null } && !string.IsNullOrWhiteSpace(p.Spec))
                pluginSpecs.Add(p);
        }

        // 按 Spec 去重
        pluginSpecs = pluginSpecs
            .GroupBy(s => s.Spec.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        // 自动加载内置扩展
        if (pluginSpecs.Count == 0)
        {
            var pluginsDll = FindSeeingAgentPluginsAssemblyPath();
            if (pluginsDll != null)
            {
                logger.LogInformation("自动加载内置扩展: {Path}", pluginsDll);
                pluginSpecs.Add(new PluginSpec { Spec = pluginsDll });
            }
            else
            {
                logger.LogWarning(
                    "未找到扩展配置且未找到 Seeing.Agent.Plugins.dll。扩展 Agent 将不会加载。");
            }
        }

        // 初始化扩展
        await extensionManager.InitializeAsync(pluginSpecs, enabledOverrides, context);

        // 更新状态
        state.ExtensionCount = extensionManager.GetAll().Count;
    }

    private static async Task ConnectMcpAsync(IServiceProvider provider, TuiState state, ILogger logger)
    {
        var mcp = provider.GetRequiredService<McpClientManager>();
        var invoker = provider.GetRequiredService<ToolInvoker>();

        state.RegisteredMcpToolIds.Clear();

        var configs = SeeingMcpConfigLoader.LoadDefault(state.WorkspaceRoot, logger);

        foreach (var cfg in configs)
        {
            try
            {
                await mcp.ConnectAsync(cfg);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "MCP 连接失败: {Name}", cfg.Name);
            }
        }

        foreach (var tool in mcp.GetToolsAsITools())
            invoker.RegisterTool(tool);

        state.McpServerNames = mcp.GetConnectedServers().ToList();
        state.McpServerCount = state.McpServerNames.Count;
        state.McpToolInfos = mcp.GetTools().ToList();

        foreach (var t in state.McpToolInfos)
            state.RegisteredMcpToolIds.Add(t.Id);
    }

    /// <summary>
    /// 在常见输出目录中查找内置插件程序集
    /// </summary>
    public static string? FindSeeingAgentPluginsAssemblyPath()
    {
        var fileName = "Seeing.Agent.Plugins.dll";
        foreach (var dir in GetCandidateBaseDirectories())
        {
            var p = Path.Combine(dir, fileName);
            if (File.Exists(p))
                return p;
        }
        return null;
    }

    private static IEnumerable<string> GetCandidateBaseDirectories()
    {
        yield return AppContext.BaseDirectory;
        yield return AppDomain.CurrentDomain.BaseDirectory;
        var loc = typeof(Program).Assembly.Location;
        if (!string.IsNullOrEmpty(loc))
        {
            var d = Path.GetDirectoryName(loc);
            if (!string.IsNullOrEmpty(d))
                yield return d;
        }
    }

    /// <summary>
    /// 从环境变量应用 API Keys
    /// </summary>
    public static void ApplyApiKeysFromEnvironment(SeeingAgentOptions options)
    {
        foreach (var p in options.Providers.Values)
        {
            if (!string.IsNullOrWhiteSpace(p.ApiKey))
                continue;

            if (p.Type == ProviderType.OpenAI)
                p.ApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            else if (p.Type == ProviderType.Anthropic)
                p.ApiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        }
    }
}