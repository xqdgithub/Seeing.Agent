using Microsoft.Extensions.Logging;
using Seeing.Agent.Configuration;
using Seeing.Agent.MCP.Configuration;

namespace Seeing.Agent.MCP;

/// <summary>
/// MCP 配置加载器 - 静态便捷入口，委托给 McpConfigPersistence
/// <para>
/// 配置文件位置：
/// - 用户级：~/.seeing/mcp.json
/// - 项目级：./.seeing/mcp.json
/// 项目级同名服务覆盖用户级
/// </para>
/// </summary>
public static class McpConfigLoader
{
    /// <summary>
    /// 加载默认路径的 MCP 配置（同步版本，用于启动时）
    /// </summary>
    public static IReadOnlyList<McpServerConfig> LoadDefault(string workspaceRoot, ILogger? logger = null)
    {
        var workspaceProvider = new WorkspaceProvider(workspaceRoot);
        return LoadDefault(workspaceProvider, logger);
    }

    /// <summary>
    /// 加载默认路径的 MCP 配置（同步版本，使用 IWorkspaceProvider）
    /// </summary>
    public static IReadOnlyList<McpServerConfig> LoadDefault(IWorkspaceProvider workspaceProvider, ILogger? logger = null)
    {
        // 创建持久化实例
        var persistence = new McpConfigPersistence(
            logger as ILogger<McpConfigPersistence> ?? new NullLogger<McpConfigPersistence>(),
            workspaceProvider);

        var configs = new Dictionary<string, McpServerConfig>(StringComparer.OrdinalIgnoreCase);

        // 先加载用户级（作为基础）
        if (persistence.ConfigExists(ConfigLevel.User))
        {
            var userConfigs = persistence.LoadAsync(ConfigLevel.User).GetAwaiter().GetResult();
            foreach (var kvp in userConfigs)
                configs[kvp.Key] = kvp.Value;
        }

        // 后加载项目级（覆盖同名服务）
        if (persistence.ConfigExists(ConfigLevel.Project))
        {
            var projectConfigs = persistence.LoadAsync(ConfigLevel.Project).GetAwaiter().GetResult();
            foreach (var kvp in projectConfigs)
                configs[kvp.Key] = kvp.Value;
        }

        return configs.Values.ToList().AsReadOnly();
    }

    /// <summary>
    /// 加载默认路径的 MCP 配置（异步版本）
    /// </summary>
    public static async Task<IReadOnlyList<McpServerConfig>> LoadDefaultAsync(
        string workspaceRoot,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        var workspaceProvider = new WorkspaceProvider(workspaceRoot);
        return await LoadDefaultAsync(workspaceProvider, logger, cancellationToken);
    }

    /// <summary>
    /// 加载默认路径的 MCP 配置（异步版本，使用 IWorkspaceProvider）
    /// </summary>
    public static async Task<IReadOnlyList<McpServerConfig>> LoadDefaultAsync(
        IWorkspaceProvider workspaceProvider,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        var persistence = new McpConfigPersistence(
            logger as ILogger<McpConfigPersistence> ?? new NullLogger<McpConfigPersistence>(),
            workspaceProvider);

        var configs = new Dictionary<string, McpServerConfig>(StringComparer.OrdinalIgnoreCase);

        // 先加载用户级
        if (persistence.ConfigExists(ConfigLevel.User))
        {
            var userConfigs = await persistence.LoadAsync(ConfigLevel.User, cancellationToken);
            foreach (var kvp in userConfigs)
                configs[kvp.Key] = kvp.Value;
        }

        // 后加载项目级
        if (persistence.ConfigExists(ConfigLevel.Project))
        {
            var projectConfigs = await persistence.LoadAsync(ConfigLevel.Project, cancellationToken);
            foreach (var kvp in projectConfigs)
                configs[kvp.Key] = kvp.Value;
        }

        return configs.Values.ToList().AsReadOnly();
    }
}

/// <summary>
/// 空 Logger 实现（用于无 logger 场景）
/// </summary>
file class NullLogger<T> : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => false;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
}