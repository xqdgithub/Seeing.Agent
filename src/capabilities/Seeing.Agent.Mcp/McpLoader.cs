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

    /// <inheritdoc />
    public string ModuleId => "mcp";

    public async Task<ComponentLoadResult> LoadAsync(
        IServiceProvider services,
        string workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        var mcpManager = services.GetRequiredService<McpClientManager>();
        var loggerFactory = services.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger<McpLoader>();

        // 已由 McpModule.Activate 初始化（模块生命周期为权威入口）时，启动阶段跳过重复初始化
        if (mcpManager.IsInitialized)
        {
            var existing = mcpManager.GetAllConfigs();
            return new ComponentLoadResult
            {
                Type = Type,
                Success = true,
                Count = existing.Count,
                Details = existing.Keys.ToList()
            };
        }

        var directories = services.GetService<ISeeingDirectories>()
            ?? new WorkspaceSeeingDirectories(workspaceRoot);

        var configs = await McpConfigLoader.LoadDefaultAsync(directories, logger, cancellationToken)
            .ConfigureAwait(false);

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

        var configs = await McpConfigLoader.LoadDefaultAsync(directories, logger, cancellationToken)
            .ConfigureAwait(false);

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
