using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Components;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Mcp;

namespace Seeing.Agent.Mcp;

/// <summary>MCP 组件加载器 — 由 <see cref="McpModule"/> 登记到 <see cref="IComponentManager"/>。</summary>
public sealed class McpLoader : IComponentLoader
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
        var directories = services.GetService<ISeeingDirectories>()
            ?? new WorkspaceSeeingDirectories(workspaceRoot);

        var configs = McpConfigLoader.LoadDefault(directories, logger);

        var configDict = new Dictionary<string, McpServerConfig>();
        foreach (var config in configs)
        {
            if (!string.IsNullOrEmpty(config.Name))
                configDict[config.Name] = config;
        }

        await mcpManager.InitializeAsync(configDict, cancellationToken);

        return new ComponentLoadResult
        {
            Type = Type,
            Success = true,
            Count = configs.Count,
            Details = configs.Select(c => c.Name).ToList()
        };
    }

    public async Task<ComponentLoadResult> ReloadAsync(
        IServiceProvider services,
        string workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        var mcpManager = services.GetRequiredService<McpClientManager>();
        var loggerFactory = services.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger<McpLoader>();
        var directories = services.GetService<ISeeingDirectories>()
            ?? new WorkspaceSeeingDirectories(workspaceRoot);

        var configs = McpConfigLoader.LoadDefault(directories, logger);

        var configDict = new Dictionary<string, McpServerConfig>();
        foreach (var config in configs)
        {
            if (!string.IsNullOrEmpty(config.Name))
                configDict[config.Name] = config;
        }

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
