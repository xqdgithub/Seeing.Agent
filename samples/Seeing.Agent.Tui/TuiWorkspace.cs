using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.MCP;
using Seeing.Agent.Rules;
using Seeing.Agent.Skills;
using Seeing.Agent.Tools;
using Seeing.Agent.Tui.Core;
using Seeing.Agent.Tui.Infrastructure;

namespace Seeing.Agent.Tui;

/// <summary>
/// 工作区初始化与 MCP 工具注册。
/// </summary>
public static class TuiWorkspace
{
    public static async Task InitializeAsync(IServiceProvider services, ILogger logger, CancellationToken cancellationToken = default)
    {
        var state = services.GetRequiredService<TuiState>();
        var registry = services.GetRequiredService<IAgentRegistry>();
        try
        {
            state.CurrentAgentKey = await registry.GetDefaultAgentNameAsync();
        }
        catch (InvalidOperationException)
        {
            var primaries = await registry.GetPrimaryAgentsAsync();
            if (primaries.Count > 0)
                state.CurrentAgentKey = primaries[0].Name;
        }

        var (rulesText, sources) = RulesMarkdownLoader.Load(state.WorkspaceRoot);
        state.RulesMarkdown = rulesText;
        state.RulesSources = sources;

        services.GetRequiredService<RuleEngine>().MergeRules(PermissionRulesFile.TryLoad(state.WorkspaceRoot));

        await SkillPathBootstrap.ApplyAsync(
            services.GetRequiredService<SkillManager>(),
            services.GetRequiredService<IOptions<SeeingAgentOptions>>(),
            state.WorkspaceRoot,
            cancellationToken);

        await ConnectMcpAndRegisterToolsAsync(services, logger, cancellationToken);
    }

    public static async Task ChangeWorkspaceAsync(IServiceProvider services, string newRoot, ILogger logger, CancellationToken cancellationToken = default)
    {
        var state = services.GetRequiredService<TuiState>();
        var mcp = services.GetRequiredService<McpClientManager>();
        var invoker = services.GetRequiredService<ToolInvoker>();

        await mcp.DisconnectAllAsync();
        foreach (var id in state.RegisteredMcpToolIds.ToArray())
            invoker.UnregisterTool(id);
        state.RegisteredMcpToolIds.Clear();

        Directory.SetCurrentDirectory(newRoot);
        state.WorkspaceRoot = newRoot;

        var (rulesText, sources) = RulesMarkdownLoader.Load(newRoot);
        state.RulesMarkdown = rulesText;
        state.RulesSources = sources;

        await SkillPathBootstrap.ReloadForWorkspaceAsync(
            services.GetRequiredService<SkillManager>(),
            services.GetRequiredService<IOptions<SeeingAgentOptions>>(),
            newRoot,
            cancellationToken);

        await ConnectMcpAndRegisterToolsAsync(services, logger, cancellationToken);
    }

    private static async Task ConnectMcpAndRegisterToolsAsync(IServiceProvider services, ILogger logger, CancellationToken cancellationToken)
    {
        var mcp = services.GetRequiredService<McpClientManager>();
        var invoker = services.GetRequiredService<ToolInvoker>();
        var state = services.GetRequiredService<TuiState>();

        var configs = SeeingMcpConfigLoader.LoadDefault(state.WorkspaceRoot, logger);

        foreach (var cfg in configs)
        {
            try
            {
                await mcp.ConnectAsync(cfg, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "MCP 连接失败: {Name}", cfg.Name);
            }
        }

        foreach (var tool in mcp.GetToolsAsITools())
        {
            invoker.RegisterTool(tool);
            state.RegisteredMcpToolIds.Add(tool.Id);
        }
    }
}
